// This program reads in tick files (like those created by ReadSierraChartSCIDSharp), and writes out files by doing two things:
// 1. compress the data by converting ticks to cenetered range bars
// 2. create additional data fields that can be used as lables or inputs in machine learning.
//
// One of the things you need to do for supervised learning is have a label for each observation. In this case the observation
// is eaach range bar. The primary label this progrm creates is, assuming you entered a trade at the price of the range bar, 
// what's the maximum percent you could make before some percentage decline. This is the "long term value" of the range bar

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace CreateRangeBars {
    internal class Tick {
        internal DateTime time;
        internal float close;
        internal int volume;
        internal float value; // target for ml: looks forward in time
    }

    static internal class Program {
        internal const string version = "CreateRangeBars 0.1.0";
        internal static string futures_root = "ES";
        internal static bool update_only = true; // only process .txt files in datafile_dir which do not have counterparts in datafile_outdir

        const string datafile_dir = "C:/Users/lel48/SierraChartData";
        const string datafile_outdir = "C:/Users/lel48/SierraChartData/RangeBars/";
        static readonly Dictionary<char, int> futures_codes = new() { { 'H', 3 }, { 'M', 6 }, { 'U', 9 }, { 'Z', 12 } };

        const int minTicks = 4000; // write out message to stats file if day has fewer than this many ticks
        const bool header = false;
        const int maxGap = 60;
        const float minPrice = 100.00f;
        const float maxPrice = 20000.00f;
        const int barSize = 1; // seconds

        // 01/05/2015,09:30:27,1192.36
        static DateTime preSessionBegTime = Convert.ToDateTime("08:00:00");
        static DateTime sessionEndTime = Convert.ToDateTime("16:00:00");
        static DateTime sessionBegTime = Convert.ToDateTime("09:30:00");
        static TimeSpan sessionStartTS = new TimeSpan(9, 30, 0);
        static DateTime firstDate = Convert.ToDateTime("1/1/2000");

        static Logger logger = new Logger(datafile_outdir);

        static int Main(string[] args) {
            if (logger.state != 0)
                return -1;

            var stopWatch = new Stopwatch();
            stopWatch.Start();

            string[] archiveNames = Directory.GetFiles(datafile_dir, futures_root + "*.zip", SearchOption.TopDirectoryOnly);
            //Parallel.ForEach(archiveNames, archiveName => ProcessTickArchive(futures_root, archiveName, logger));
            ProcessTickArchive(futures_root, archiveNames[0], logger); // debug
            logger.close();

            stopWatch.Stop();
            Console.WriteLine($"Elapsed time = {stopWatch.Elapsed}");

            return 0;
        }

        static int ProcessTickArchive(string futures_root, string archiveName, Logger logger) {
            string? row;
            DateTime lastDateTime = DateTime.MinValue;
            bool newDay = false;
            int numTimeGaps = 0;
            int maxTimeGap = 0;
            float cumValue = 0f;

            var archive_filename = Path.GetFullPath(archiveName);
            var futures_contract = Path.GetFileNameWithoutExtension(archiveName);
            (int rc, string out_path) = ValidateFuturesFilename(futures_contract);
            if (rc < 0)
                return -1;

            using (ZipArchive archive = ZipFile.OpenRead(archive_filename)) {
                if (archive.Entries.Count != 1) {
                    logger.log(1, "There must be only one entry in each zip file: " + archive_filename);
                    return -1;
                }
                ZipArchiveEntry zip = archive.Entries[0];

                Console.WriteLine("Processing archive " + archive_filename);

                int numLines = 0;
                using (StreamReader reader = new StreamReader(zip.Open())) {
#if false // no header written yet
                    string? header = reader.ReadLine();
                    if (header == null) {
                        logger.log(2, zip.Name + " is empty.");
                        return -1;
                    }
#endif
                    using (StreamWriter writer = new StreamWriter(out_path + ".csv")) {
                        List<Tick> ticks = new List<Tick>();
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
                                    Parallel.ForEach(ticks, tick => ProcessTick(ticks));

                                    // compute total value for day
                                    float cumValueOfDay = 0f;
                                    foreach (Tick t in ticks)
                                        cumValueOfDay += t.value;
                                    Console.WriteLine($"Value of {lastDateTime:d} is {cumValueOfDay}");

                                    cumValue += cumValueOfDay;
                                }

                                WriteTicks(writer, ticks);
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
                                            newTick.time = fakeTickTime;
                                            fakeTickTime = fakeTickTime.AddSeconds(barSize);
                                            newTick.close = tick.close;
                                            ticks.Add(newTick);
                                        }
                                    }
                                    ticks.Add(tick);
                                }
                            }

                            lastDateTime = tick.time;
                        }
                    }
                }

                Console.WriteLine($"Total Value is {cumValue}");

                return 0;
            }
        }

        static (int rc, string out_path) ValidateFuturesFilename(string filename) {
            char futures_code = filename[futures_root.Length];
            if (!futures_codes.ContainsKey(futures_code)) {
                logger.log(2, "Malformed futures file name: " + filename);
                return (-1, "");
            }

            // get 4 digit futures year from zip filename (which has 2 digit year)
            string futures_two_digit_year_str = filename.Substring(futures_root.Length + 1, 2);
            if (!Char.IsDigit(futures_two_digit_year_str[0]) || !Char.IsDigit(futures_two_digit_year_str[1])) {
                logger.log(2, "Malformed futures file name: " + filename);
                return (-1, "");
            }

            int futures_year;
            bool parse_suceeded = Int32.TryParse(futures_two_digit_year_str, out futures_year);
            if (!parse_suceeded) {
                logger.log(2, "Malformed futures file name: " + filename);
                return (-1, "");
            }

            string out_fn_base = futures_root + futures_code + futures_two_digit_year_str;
            string out_path = datafile_outdir + out_fn_base;

            return (0, out_path);
        }

        static void WriteTicks(StreamWriter sw, List<Tick> ticks) {
            foreach (Tick tick in ticks) {
                sw.WriteLine($"{tick.time:d},{tick.time:HH:mm:ss},{tick.close:F2},{tick.value:F2}");
            }
        }

        static void ProcessTick(List<Tick> ticks) {
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
            if (cols.Length < 2) {
                Console.WriteLine($"Line {lineno} has fewer than 2 columns. Line ignored.");
                return false;
            }

            bool rc = DateTime.TryParseExact(cols[0], "s", null, DateTimeStyles.None, out tick.time);
            if (!rc) {
                Console.WriteLine($"Line {lineno} has invalid date/time: {cols[0]}. Line ignored.");
                return false;
            }

            rc = float.TryParse(cols[5], out tick.close);
            if (!rc | tick.close < minPrice) {
                Console.WriteLine($"Line {lineno} has invalid close: {cols[5]}. Line ignored.");
                return false;
            }
#if false
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
#endif
            return true;
        }
    }
}
