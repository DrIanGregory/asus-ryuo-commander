// Both WPF (System.Windows) and WinForms (System.Windows.Forms) are referenced
// (WPF for the UI, WinForms for the tray NotifyIcon). These aliases make the
// common ambiguous type names resolve to their WPF versions everywhere.
global using System.IO; // WindowsDesktop SDK implicit usings omit System.IO
global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
