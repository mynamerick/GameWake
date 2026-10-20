using System.Runtime.InteropServices;
using System.Xml.Linq;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace GameWake.App.Services;

/// <summary>
/// Shows desktop notifications for unpackaged GameWake. Toasts are preferred; the tray host
/// falls back to a balloon tip when Windows will not deliver a toast.
/// </summary>
public static class DesktopNotifier
{
    public const string AppUserModelId = "GameWake.Desktop";

    public static void Initialize()
    {
        NativeMethods.SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
    }

    public static bool TryShow(string title, string message)
    {
        try
        {
            var xml = new XElement("toast",
                new XElement("visual",
                    new XElement("binding", new XAttribute("template", "ToastGeneric"),
                        new XElement("text", title ?? string.Empty),
                        new XElement("text", message ?? string.Empty))));

            var document = new XmlDocument();
            document.LoadXml(xml.ToString(SaveOptions.DisableFormatting));
            ToastNotificationManager.CreateToastNotifier(AppUserModelId).Show(new ToastNotification(document));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int SetCurrentProcessExplicitAppUserModelID(string appID);
    }
}
