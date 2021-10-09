//
// yes...a hand coded Logger class...but this is really a small, lightweight app
// I just need: datetime, code, string
//

using System;
using System.Diagnostics;
using System.IO;

namespace CreateRangeBars;

class Logger {
    StreamWriter? outputFile = null;

    internal Logger(string datafile_dir) {
        try {
            string log_path = Path.Combine(datafile_dir, "Logs/");
            if (!Directory.Exists(log_path)) {
                Directory.CreateDirectory(log_path);
            }
            string dt_str = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string log_filename = log_path + "ReadSierraChartDataSharp_" + dt_str + ".txt";
            outputFile = new StreamWriter(log_filename);
        }
        catch (Exception ex) {
            Console.WriteLine("Unable to create log file in :" + datafile_dir + "\n Message: " + ex.Message);
            System.Environment.Exit(-1);
        }
    }

    // returns 0 if normal message, -1 if error message (code < 0)
    internal void log(ReturnCodes code, string message) {
        Debug.Assert(message.Length > 0);
        if (outputFile != null) {
            string dt_str = DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture);
            Debug.Assert(dt_str.Length > 0);
            outputFile.WriteLine($"{dt_str},{code},{message}");
        }
    }

    internal void close() {
        outputFile?.Close();
    }
}
