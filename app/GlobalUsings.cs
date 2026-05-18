// Resolve WPF vs WinForms namespace ambiguities when UseWindowsForms is enabled
global using Application = System.Windows.Application;
global using UserControl = System.Windows.Controls.UserControl;
global using MessageBox  = System.Windows.MessageBox;
global using Clipboard   = System.Windows.Clipboard;
global using Binding     = System.Windows.Data.Binding;
