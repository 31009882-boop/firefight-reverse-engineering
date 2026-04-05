using System.Text;
using System.Windows.Forms;

namespace FirefightDesktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            var assetRootOverride = ParseAssetRootOverride(args);
            var assetRoot = AssetLocator.FindAssetRoot(assetRootOverride);
            Application.Run(new FirefightDesktopForm(assetRoot));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"{ex.Message}{Environment.NewLine}{Environment.NewLine}{ex}",
                "FirefightDesktop failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static string? ParseAssetRootOverride(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].Equals("--assets", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
