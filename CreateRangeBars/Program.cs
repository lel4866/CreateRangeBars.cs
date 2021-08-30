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

namespace CreateRangeBars;

// warnings are greater than 0, errors are less than 0
enum ReturnCodes {
    Successful = 0,
    FileIgnored = 1,
    MalformedFuturesFileName = -1,
    IOErrorReadingData = -2,
    MultipleFilesInZipFile = -3
}

class Tick {
    internal DateTime time;
    internal float close;
    internal int volume;
    internal float value; // target for ml: looks forward in time
}

static class Program {
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
    const float maxPrice = 100000.00f;
    const int barSize = 1; // seconds

    // 01/05/2015,09:30:27,1192.36
    static DateTime preSessionBegTime = Convert.ToDateTime("08:00:00");
    static DateTime sessionEndTime = Convert.ToDateTime("16:00:00");
    static DateTime sessionBegTime = Convert.ToDateTime("09:30:00");
    static TimeSpan sessionStartTS = new TimeSpan(9, 30, 0);
    static DateTime firstDate = Convert.ToDateTime("1/1/2000");

    static readonly TimeSpan four_thirty_pm = new(16, 30, 0); // session end (Eastern/US)
    static readonly TimeSpan six_pm = new(18, 0, 0); // session start (Eastern/US)

    static internal Logger logger = new(datafile_outdir); // this could call System.Environment.Exit
    static int return_code = 0;

    static int Main(string[] args) {
        var stopWatch = new Stopwatch();
        stopWatch.Start();

        try {
            string[] archiveNames = Directory.GetFiles(datafile_dir, futures_root + "*.zip", SearchOption.TopDirectoryOnly);
            //Parallel.ForEach(archiveNames, archiveName => ProcessTickArchive(archiveName));
            ProcessTickArchive(archiveNames[0]); // debug - just process one archive
        }
        finally {
            logger.close();
        }

        stopWatch.Stop();
        Console.WriteLine($"Elapsed time = {stopWatch.Elapsed}");

        return return_code;
    }

    // returns 0 if (success OR FileIgnored due to update_only mode), -1 for malformed file names, IO error 
    // also sets global return_code to -1 if return value is -1
    static int ProcessTickArchive(string archive_name) {
        string? row;
        bool newDay = false;
        int numTimeGaps = 0;
        int maxTimeGap = 0;
        float cumValue = 0f;

        // make sure futures filename has form: {futures_root}{month_code}{2 digit year}
        string fn_base = Path.GetFileNameWithoutExtension(archive_name);
        if (ValidateFuturesFilename(fn_base, out int futures_year, out char futures_code) != 0)
            return -1;

        // get filenames for temporary .csv output file and final .zip file
        string out_path = datafile_outdir + fn_base;
        string out_path_csv = out_path + ".csv"; // full path
        string out_path_zip = out_path + ".zip"; // full path

        // if update_only is true and file already exists in datafile_outdir, ignore it
        if (update_only) {
            if (File.Exists(out_path_zip))
                return log(ReturnCodes.FileIgnored, "Update only mode; file ignored: " + archive_name);
        }

        // open zip file. There can only be a single .csv file in zip file
        using (ZipArchive archive = ZipFile.OpenRead(archive_name)) {
            if (archive.Entries.Count != 1)
                return log(ReturnCodes.MultipleFilesInZipFile, "There must be only one entry in each zip file: " + archive_name);
            ZipArchiveEntry zip = archive.Entries[0];
            Console.WriteLine("Processing archive " + archive_name);

            int numLines = 0;
            TimeSpan lastTime = new();
            using (StreamReader reader = new StreamReader(zip.Open())) {
                using (StreamWriter writer = new StreamWriter(out_path_csv)) {
                    List<Tick> ticks = new List<Tick>();
                    while ((row = reader.ReadLine()) != null) {
                        numLines++;
                        Tick tick = new Tick();
                        if (!validateRow(row, numLines, tick))
                            continue;

                        // reset previous time if session start
                        TimeSpan time = tick.time.TimeOfDay;
                        if (time == six_pm)
                            lastTime = time;
                        TimeSpan time_diff = time - lastTime;

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
                    Console.WriteLine($"Total Value is {cumValue}");
                }
            }
        }

        // create output zip file and add csv created above to it
        File.Delete(out_path_zip); // in case zio file already exists...needed or ZipFile.Open with ZipArchiveMode.Create could fail
        using (ZipArchive archive = ZipFile.Open(out_path_zip, ZipArchiveMode.Create)) {
            archive.CreateEntryFromFile(out_path_csv, Path.GetFileName(out_path_csv));
        }
        File.Delete(out_path_csv); // delete csv file

        return log(ReturnCodes.Successful, out_path_zip + " created.");
    }


    // make sure filename is of form: {futures_root}{month_code}{2 digit year}
    static int ValidateFuturesFilename(string fn_base, out int futures_year, out char futures_code) {
        futures_year = 0;

        // make sure filename has a valid futures code: 'H', 'M', 'U', 'Z'
        futures_code = fn_base[futures_root.Length];
        if (!futures_codes.ContainsKey(futures_code))
            return log(ReturnCodes.MalformedFuturesFileName, "Malformed futures file name: " + fn_base + ".zip");

        // get 4 digit futures year from .scid filename (which has 2 digit year)
        string futures_two_digit_year_str = fn_base.Substring(futures_root.Length + 1, 2);
        if (!Char.IsDigit(futures_two_digit_year_str[0]) || !Char.IsDigit(futures_two_digit_year_str[1]))
            return log(ReturnCodes.MalformedFuturesFileName, "Malformed futures file name: " + fn_base + ".zip");

        bool parse_suceeded = Int32.TryParse(futures_two_digit_year_str, out futures_year);
        if (!parse_suceeded)
            return log(ReturnCodes.MalformedFuturesFileName, "Malformed futures file name: " + fn_base + ".zip");
        futures_year += 2000;
        return 0;
    }

    static void WriteTicks(StreamWriter sw, List<Tick> ticks) {
        foreach (Tick tick in ticks) {
            sw.WriteLine($"{tick.time:d},{tick.time:HH:mm:ss},{tick.close:F2},{tick.value:F2}");
        }
    }

    // starting from specified tick, see how much money you make/lose going forward
    static void ProcessTick(int tickIndex, List<Tick> ticks) {
        // search forward until you eithe make or lose $100. Assume $50 per point
        float value = 0.0f;
        float maxValue = 0.0f;
        float stop = 50.0f;
        float percentStop = 0.25f;
        float close = ticks[0].close;
        for (int i = 0; i < ticks.Count; i++) {
            value = 50.0f * (ticks[i].close - close);
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
        if (!rc || tick.close < minPrice || tick.close > maxPrice) {
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


    // thread safe setting of return_code
    static int log(ReturnCodes code, string message) {
        logger.log(code, message);
        int rc = code < 0 ? -1 : 0;
        if (rc < 0)
            Interlocked.Exchange(ref return_code, rc);
        return rc;
    }
}
