//
// yes...a hand coded Logger class...but this is really a small, lightweight app
// I just need: datetime, code, string
//

using System.Diagnostics;

namespace CreateRangeBars {
    class Logger {
        internal int state = -1;
        internal ReturnCodes worst_code = ReturnCodes.Successful;
        StreamWriter? outputFile;

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
                state = -2;
                return;
            }

            // log file created
            state = 0;
        }

        internal ReturnCodes log(ReturnCodes code, string message) {
            Debug.Assert(message.Length > 0);
            Debug.Assert(state == 0);
            if (outputFile != null) {
                string dt_str = DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture);
                Debug.Assert(dt_str.Length > 0);
                outputFile.WriteLine($"{dt_str},{code},{message}");
            }

            if (code < worst_code)
                worst_code = code;
            return code;
        }

        internal void close() {
            if (outputFile != null)
                outputFile.Close();
            state = -1;
        }
    }
}
