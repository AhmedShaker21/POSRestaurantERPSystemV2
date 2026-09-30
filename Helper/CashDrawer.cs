using System;
using System.Runtime.InteropServices;

namespace RestaurantERP.Helpers
{
    public static class CashDrawer
    {
        [DllImport("winspool.Drv", EntryPoint = "OpenPrinterA", SetLastError = true)]
        private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In] DOCINFOA di);

        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool EndDocPrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool StartPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool EndPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool WritePrinter(IntPtr hPrinter, byte[] data, int count, out int written);

        [StructLayout(LayoutKind.Sequential)]
        private class DOCINFOA
        {
            public string pDocName = "Open Cash Drawer";
            public string? pOutputFile = null;
            public string pDataType = "RAW";
        }

        public static void OpenDrawer()
        {
            string printerName = "80 Printer Series"; // نفس اسم الطابعة عندك بالظبط

            byte[] command = new byte[] { 27, 112, 0, 25, 250 };

            if (!OpenPrinter(printerName, out IntPtr hPrinter, IntPtr.Zero))
                throw new Exception("Printer not found or cannot open printer.");

            try
            {
                var docInfo = new DOCINFOA();

                if (!StartDocPrinter(hPrinter, 1, docInfo))
                    throw new Exception("Cannot start printer document.");

                try
                {
                    StartPagePrinter(hPrinter);
                    WritePrinter(hPrinter, command, command.Length, out int written);
                    EndPagePrinter(hPrinter);
                }
                finally
                {
                    EndDocPrinter(hPrinter);
                }
            }
            finally
            {
                ClosePrinter(hPrinter);
            }
        }
    }
}