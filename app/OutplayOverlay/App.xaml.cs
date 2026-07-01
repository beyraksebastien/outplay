using System.Windows;

namespace OutplayOverlay;

// Fully qualified: with UseWindowsForms=true alongside UseWPF=true, "Application" is ambiguous
// between System.Windows.Application and System.Windows.Forms.Application (CS0104).
public partial class App : System.Windows.Application
{
}
