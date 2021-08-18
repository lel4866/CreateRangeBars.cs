

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace CreateRangeBars {
    internal class Tick {
        internal int index;
        internal DateTime time;
        internal float open;
        internal float high;
        internal float low;
        internal float close;
        internal int volume;
        internal float value; // target for ml: looks forward in time
    }

    static internal class Program {
        internal const string version = "CreateRangeBars 0.1.0";
        internal static string futures_root = "ES";
        internal static bool update_only = true; // only process .scid files in datafile_dir which do not have counterparts in datafile_outdir
        
        const string datafile_dir = "C:/SierraChart/Data/";
        const string datafile_outdir = "C:/Users/lel48/SierraChartData/";
        static readonly Dictionary<char, int> futures_codes = new() { { 'H', 3 }, { 'M', 6 }, { 'U', 9 }, { 'Z', 12 } };

        const string userid = "lel48";
        const string baseDir = @"C:\Users\" + userid + @"\MarketData\";
        const string ArchiveName = baseDir + "RTY_5sec_2020_03_23_to_2020_09_21.zip";
        const string outFilename = baseDir + "RTY_5sec_target_2020_03_23_to_2020_09_21.txt";
        const int minTicks = 4000; // write out message to stats file if day has fewer than this many ticks
        const bool header = false;
        const int maxGap = 60;
        const float minPrice = 90.00f;
        const float maxPrice = 1000.00f;
        const int barSize = 5; // seconds

        // 01/05/2015,09:30:27,1192.36
        static DateTime preSessionBegTime = Convert.ToDateTime("08:00:00");
        static DateTime sessionEndTime = Convert.ToDateTime("16:00:00");
        static DateTime sessionBegTime = Convert.ToDateTime("09:30:00");
        static TimeSpan sessionStartTS = new TimeSpan(9, 30, 0);
        static DateTime firstDate = Convert.ToDateTime("1/1/2000");
        static DateTime checkDate = Convert.ToDateTime("9/18/2020");

        static int Main(string[] args) {
            var stopWatch = new Stopwatch();
            stopWatch.Start();

            var logger = new Logger(datafile_outdir);
            if (logger.state != 0)
                return -1;
            string[] filenames = Directory.GetFiles(datafile_dir, futures_root + "*.scid", SearchOption.TopDirectoryOnly);
            string[] existing_filenames = Directory.GetFiles(datafile_outdir, futures_root + "*.scid", SearchOption.TopDirectoryOnly);
            Parallel.ForEach(filenames, filename => ProcessTickFile(futures_root, filename, logger));
            logger.close();

            stopWatch.Stop();
            Console.WriteLine($"Elapsed time = {stopWatch.Elapsed}");

            return 0;
        }

        static void ProcessTickFile(string futures_root, string filename, Logger logger) { 
            string? row;
            DateTime lastDateTime = DateTime.MinValue;
            bool newDay = false;
            int numTimeGaps = 0;
            int maxTimeGap = 0;
            float cumValue = 0f;

            StreamWriter sw = new StreamWriter(outFilename);

            using (ZipArchive archive = ZipFile.OpenRead(ArchiveName)) {
                if (archive.Entries.Count != 1)
                    throw new Exception($"There must be only one entry in each zip file: {ArchiveName}");
                ZipArchiveEntry zip = archive.Entries[0];
                Console.WriteLine($"Processing archive {zip.Name}");

                int numLines = 0;
                Stopwatch timePerRead = Stopwatch.StartNew();
                using (StreamReader reader = new StreamReader(zip.Open())) {
                    string? header = reader.ReadLine();
                    if (header == null) {
                        Console.WriteLine($"{zip.Name} is empty.");
                        return;
                    }

                    List<Tick> ticks = new List<Tick>();
                    int tickIndex = 0;
                    while ((row = reader.ReadLine()) != null) {
                        numLines++;
                        Tick tick = new Tick();
                        if (!validateRow(row, numLines, tick))
                            continue;

                        TimeSpan dtDiff = tick.time.Date - lastDateTime.Date;

                        // see if this starts new day
                        if (tick.time.Date != lastDateTime.Date) {
                            newDay = true;
                            if (ticks.Count > 0) {
                                Parallel.ForEach(ticks, tick => ProcessTick(ticks, tick.index));


                                // compute total value for day
                                float cumValueOfDay = 0f;
                                foreach (Tick t in ticks)
                                    cumValueOfDay += t.value;
                                Console.WriteLine($"Value of {lastDateTime:d} is {cumValueOfDay}");

                                cumValue += cumValueOfDay;
                            }

                            WriteTicks(sw, ticks);
                            ticks.Clear();
                            tickIndex = 0;
                        }
                        else {
                            if ((tick.time.TimeOfDay >= preSessionBegTime.TimeOfDay && (tick.time.TimeOfDay < sessionEndTime.TimeOfDay))) {
                                // see if we've skipped bar interval
                                TimeSpan tDiff = tick.time - lastDateTime;
                                int iDiff = (int)tDiff.TotalSeconds;
                                if (iDiff > barSize) {
                                    // add fake ticks
                                    DateTime fakeTickTime = lastDateTime.AddSeconds(barSize);
                                    while (fakeTickTime < tick.time) {
                                        Tick newTick = new Tick();
                                        newTick.index = tickIndex++;
                                        newTick.time = fakeTickTime;
                                        fakeTickTime = fakeTickTime.AddSeconds(barSize);
                                        newTick.open = newTick.high = newTick.low = newTick.close = tick.close;
                                        ticks.Add(newTick);
                                    }
                                }
                                tick.index = tickIndex++;
                                ticks.Add(tick);
                            }
                        }

                        lastDateTime = tick.time;
                    }
                }

                sw.Close();
                timePerRead.Stop();
                float elapsedTime = timePerRead.ElapsedMilliseconds / 1000.0f;
                Console.WriteLine($"Total Value is {cumValue}");
                Console.WriteLine($"Read {numLines} lines in {elapsedTime} seconds.");
            }
        }

        static void WriteTicks(StreamWriter sw, List<Tick> ticks) {
            foreach (Tick tick in ticks) {
                sw.WriteLine($"{tick.time:d},{tick.time:HH:mm:ss},{tick.close:F2},{tick.value:F2}");
            }
        }

        static void ProcessTick(List<Tick> ticks, int index) {
            // search forward until you eithe make or lose $100. Assume $50 per point
            int phase = 0;
            float value = 0.0f;
            float maxValue = 0.0f;
            float stop = 50.0f;
            float percentStop = 0.25f;
            float openPrice = ticks[index].close;
            for (int i = index + 1; i < ticks.Count; i++) {
                value = 50.0f * (ticks[i].close - openPrice);
                maxValue = Math.Max(value, maxValue);

                if (value <= maxValue - stop)
                    break;
                else if (value > 200.0)
                    stop = value * percentStop;
            }
            ticks[index].value = value;
        }

        static bool validateRow(string row, int lineno, Tick tick) {
            // validate row
            string[] cols = row.Split(',');
            if (cols.Length < 6) {
                Console.WriteLine($"Line {lineno} has fewer than 6 columns. Line ignored.");
                return false;
            }

            string sdt = cols[0] + ',' + cols[1];
            bool rc = DateTime.TryParseExact(sdt, "MM/dd/yyyy,HH:mm:ss", null, DateTimeStyles.None, out tick.time);
            if (!rc) {
                Console.WriteLine($"Line {lineno} has invalid date/time: {sdt}. Line ignored.");
                return false;
            }

            rc = float.TryParse(cols[2], out tick.open);
            if (!rc | tick.open < minPrice) {
                Console.WriteLine($"Line {lineno} has invalid open: {cols[2]}. Line ignored.");
                return false;
            }

            rc = float.TryParse(cols[3], out tick.high);
            if (!rc | tick.high < minPrice) {
                Console.WriteLine($"Line {lineno} has invalid high: {cols[3]}. Line ignored.");
                return false;
            }

            rc = float.TryParse(cols[4], out tick.low);
            if (!rc | tick.low < minPrice) {
                Console.WriteLine($"Line {lineno} has invalid low: {cols[4]}. Line ignored.");
                return false;
            }

            rc = float.TryParse(cols[5], out tick.close);
            if (!rc | tick.close < minPrice) {
                Console.WriteLine($"Line {lineno} has invalid close: {cols[5]}. Line ignored.");
                return false;
            }

            rc = int.TryParse(cols[6], out tick.volume);
            if (!rc | tick.volume < 0) {
                Console.WriteLine($"Line {lineno} has invalid volume: {cols[6]}. Line ignored.");
                return false;
            }

            if (tick.open < tick.low) {
                Console.WriteLine($"Line {lineno} open < low: {cols[2]} < {cols[4]}. Line ignored.");
                return false;

            }

            if (tick.close < tick.low) {
                Console.WriteLine($"Line {lineno} close < low: {cols[5]} < {cols[4]}. Line ignored.");
                return false;

            }

            if (tick.high < tick.low) {
                Console.WriteLine($"Line {lineno} high < low: {cols[3]} < {cols[4]}. Line ignored.");
                return false;

            }

            if (tick.open > tick.high) {
                Console.WriteLine($"Line {lineno} open > high: {cols[2]} > {cols[3]}. Line ignored.");
                return false;

            }

            if (tick.open > tick.high) {
                Console.WriteLine($"Line {lineno} close > high: {cols[5]} > {cols[3]}. Line ignored.");
                return false;

            }

            return true;
        }
    }
}
