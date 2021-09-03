// This program reads in tick files (like those created by ReadSierraChartSCIDSharp), and writes out files by doing two things:
// 1. compress the data by converting ticks to cenetered range bars
// 2. create additional data fields that can be used as lables or inputs in machine learning.
//
// One of the things you need to do for supervised learning is have a label for each observation. In this case the observation
// is each range bar. The primary label this progrm creates is, assuming you entered a trade at the price of the range bar, 
// what's the maximum percent you could make before some percentage decline. This is the "long term value" of the range bar.
// There are many other ways to determine value. This is a simple way
//
// When determining vaue, we do it using the midpoint of the range bar. While this doesn't match the training data exactly, the
// point of creating the range bars in the first place is the assumption that the fluctuations around the midpoint are random,
// and contain no usable information. So, determining value using the midpoint is not exact, but reasonable, given that the price
// history used when training will never repeat itself exactly.

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
    FileHasLessThanMinTicks = 2,
    MalformedFuturesFileName = -1,
    IOErrorReadingData = -2,
    MultipleFilesInZipFile = -3
}

struct Tick {
    internal DateTime time = new(); // time of start of tick
    internal float close = 0f;
    internal int volume = 0;
    internal float value = 0f; // target for ml: looks forward in time
}

static class Program {
    internal const string version = "CreateRangeBars 0.1.0";

    internal static string futures_root = "ES";
    const float tick_size = 0.25f;
    const float tick_range = 1f; // size of range bar = (2*tickrange + 1)*tick_size

    internal static bool update_only = true; // only process .txt files in datafile_dir which do not have counterparts in datafile_outdir
    const string datafile_dir = "C:/Users/lel48/SierraChartData";
    const string datafile_outdir = "C:/Users/lel48/SierraChartData/RangeBars/";
    static readonly Dictionary<char, int> futures_codes = new() { { 'H', 3 }, { 'M', 6 }, { 'U', 9 }, { 'Z', 12 } };

    const int minTicks = 100; // write out message to stats file if day has fewer than this many ticks
    const bool header = false;
    const int maxGap = 60;
    const float minPrice = 100.00f;
    const float maxPrice = 100000.00f;
    const int barSize = 1; // seconds

    // value of each tick is percent gain before x% loss
    // there are lots of different ways to determine this...I'm starting with a simple way
    // another way is to set a gain  where you add a break even stop and then use a trailing stop
    // even more complicated is to add a max time, that is, %gain until an x% loss or until y minutes pass
    const float maxPercentLoss = 0.2f; // so, if futures are at 1000, this is 2pts ($100) , 2000 is 4pts ($200), 4000 is 8pts ($400)

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

        using (ZipArchive archive = ZipFile.OpenRead(archive_name)) {
            // open zip file. There can only be a single .csv file in zip file
            if (archive.Entries.Count != 1)
                return log(ReturnCodes.MultipleFilesInZipFile, "There must be only one entry in each zip file: " + archive_name);
            ZipArchiveEntry zip = archive.Entries[0];
            Console.WriteLine("Processing archive " + archive_name);

            using (StreamReader reader = new StreamReader(zip.Open())) {
                using (StreamWriter writer = new StreamWriter(out_path_csv)) {
                    int numLines = 0;
                    DateTime session_start = new();
                    DateTime session_end = new();
                    DateTime prev_time = new();
                    TimeSpan maxTimeGap = new(0);
                    bool first_tick = true;
                    Tick range_bar = new();
                    Tick tick = new();
                    List<Tick> ticks = new List<Tick>();

                    while ((row = reader.ReadLine()) != null) {
                        numLines++;
                        if (!validateRow(row, numLines, ref tick))
                            continue;

                        // check for ist tick of file
                        if (first_tick) {
                            prev_time = session_start = range_bar.time = tick.time;
                            session_end = new DateTime(session_start.Year, session_start.Month, session_start.Day, 16, 30, 0);
                            if (session_start.TimeOfDay > four_thirty_pm)
                                session_end = session_end.AddDays(1); // session ends at 4:30p the next day

                            range_bar.close = tick.close;
                            first_tick = false;
                            continue;
                        }

                        // if session end, write ticks from prior session
                        TimeSpan time = tick.time.TimeOfDay;
                        if (tick.time > session_end) {
                            // add last range bar of session
                            ticks.Add(range_bar);

                            // get value of each tick in session...the maximum gain before an x% loss
                            GetValueForEachTickInSession(ticks);

                            // write ticks from session
                            WriteTicks(session_start, writer, ticks);

                            // initialize values for new session
                            ticks = new List<Tick>();
                            prev_time = session_start = range_bar.time = tick.time;
                            session_end = new DateTime(session_start.Year, session_start.Month, session_start.Day, 16, 30, 0);
                            if (session_start.TimeOfDay > four_thirty_pm)
                                session_end = session_end.AddDays(1); // session ends at 4:30p the next day

                            range_bar.close = tick.close;

                            maxTimeGap = new(0);
                            continue;
                        }

                        // check for maximum gap during session
                        TimeSpan time_diff = tick.time - prev_time;
                        if (time_diff > maxTimeGap)
                            maxTimeGap = time_diff;

                        // if new tick is outside range of range bar, save current range bar, add new range bar
                        if ((tick.close > range_bar.close + tick_range * tick_size) || (tick.close < range_bar.close - tick_range * tick_size)) {
                            // add prior range bar
                            ticks.Add(range_bar);
                            range_bar.time = tick.time;
                            range_bar.close = tick.close;
                        }
                    }

                    // get value of each tick in session...the maximum gain before an x% loss
                    GetValueForEachTickInSession(ticks);

                    // write ticks from last open session
                    WriteTicks(session_start, writer, ticks);
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

    // the value of a tick is the amount of money you make going forward before you get an x% drawdown from maximum
    static void GetValueForEachTickInSession(List<Tick> ticks) {
        for (int it = 0; it < ticks.Count; it++) {
            float value, starting_value, max_value;
            starting_value = max_value = ticks[it].close;
            for (int i = it + 1; i < ticks.Count; i++) {
                value = ticks[i].close - starting_value;
                if (value > max_value)
                    max_value = value;
                else if ((1f - value / max_value) > maxPercentLoss)
                    break;
            }
            Tick tick = ticks[it];
            tick.value = max_value;
            ticks[it] = tick;
        }
    }

    static void WriteTicks(DateTime session_start, StreamWriter sw, List<Tick> ticks) {
        if (ticks.Count < minTicks) {
            log(ReturnCodes.FileHasLessThanMinTicks, $"Session starting at {session_start} has less than {minTicks} ticks.");
            return;
        }

        foreach (Tick tick in ticks) {
            sw.WriteLine($"{tick.time:d},{tick.time:HH:mm:ss},{tick.close:F2},{tick.value:F2}");
        }
    }

#if false
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
#endif

    static bool validateRow(string row, int lineno, ref Tick tick) {

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

        rc = float.TryParse(cols[1], out tick.close);
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
