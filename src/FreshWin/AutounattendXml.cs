// FreshWin (c) Ze'ev Russak — Apache License 2.0 (see LICENSE + NOTICE).
// Derived from ZuTM's AutounattendXml. Generates the autounattend.xml that
// turns a Windows 11 ISO (tiny11 or stock) into a fully unattended install:
// LabConfig requirement bypasses, disk layout, local admin test user,
// autologon, and a FirstLogonCommands entry that locates the QEMU
// virtual-FAT stick and runs the provisioning script from it. Pure — the
// unit tests pin the structure down.

using System.Xml.Linq;

namespace FreshWin;

/// <summary>Serial marker lines the guest scripts emit on COM1; the host waits for these.</summary>
public static class GuestMarkers
{
    public const string ProvisionStart = "FRESHWIN-STAGE:provision-start";
    public const string ProvisionDone = "FRESHWIN-STAGE:provision-done";
    public const string RunnerReady = "FRESHWIN-RUNNER-READY";
    public const string NoHarness = "FRESHWIN-RUNNER:NO-HARNESS";
    public const string Shutdown = "FRESHWIN-RUNNER:SHUTDOWN";
}

public static class AutounattendXml
{
    private static readonly XNamespace Un = "urn:schemas-microsoft-com:unattend";
    private static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";

    public static string Generate(
        GuestArch arch,
        string adminUser,
        string adminPassword,
        string computerName,
        string imageName,
        string provisioningScript)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adminUser);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminPassword);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(provisioningScript);

        var processorArchitecture = arch == GuestArch.X64 ? "amd64" : "arm64";

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement(Un + "unattend",
                WindowsPePass(processorArchitecture, imageName),
                SpecializePass(processorArchitecture, computerName),
                OobePass(processorArchitecture, adminUser, adminPassword, provisioningScript)));

        return document.Declaration + Environment.NewLine + document;
    }

    private static XElement Component(string name, string processorArchitecture, params object[] content) => new(
        Un + "component",
        new XAttribute("name", name),
        new XAttribute("processorArchitecture", processorArchitecture),
        new XAttribute("publicKeyToken", "31bf3856ad364e35"),
        new XAttribute("language", "neutral"),
        new XAttribute("versionScope", "nonSxS"),
        content);

    private static XElement Pass(string name, params object[] content) => new(
        Un + "settings", new XAttribute("pass", name), content);

    private static XElement WindowsPePass(string processorArchitecture, string imageName) => Pass("windowsPE",
        Component("Microsoft-Windows-International-Core-WinPE", processorArchitecture,
            new XElement(Un + "SetupUILanguage",
                new XElement(Un + "UILanguage", "en-US")),
            new XElement(Un + "InputLocale", "0409:00000409"),
            new XElement(Un + "SystemLocale", "en-US"),
            new XElement(Un + "UILanguage", "en-US"),
            new XElement(Un + "UserLocale", "en-US")),
        Component("Microsoft-Windows-Setup", processorArchitecture,
            new XElement(Un + "DiskConfiguration",
                new XElement(Un + "WillShowUI", "OnError"),
                new XElement(Un + "Disk", new XAttribute(Wcm + "action", "add"),
                    new XElement(Un + "DiskID", 0),
                    new XElement(Un + "WillWipeDisk", true),
                    new XElement(Un + "CreatePartitions",
                        CreatePartition(1, "EFI", sizeMb: 300),
                        CreatePartition(2, "MSR", sizeMb: 128),
                        CreatePartition(3, "Primary", extend: true)),
                    new XElement(Un + "ModifyPartitions",
                        ModifyPartition(1, 1, "FAT32", "System"),
                        ModifyPartition(2, 3, "NTFS", "Windows", 'C')))),
            new XElement(Un + "ImageInstall",
                new XElement(Un + "OSImage",
                    new XElement(Un + "InstallFrom",
                        new XElement(Un + "MetaData", new XAttribute(Wcm + "action", "add"),
                            new XElement(Un + "Key", "/IMAGE/NAME"),
                            new XElement(Un + "Value", imageName))),
                    new XElement(Un + "InstallTo",
                        new XElement(Un + "DiskID", 0),
                        new XElement(Un + "PartitionID", 3)),
                    new XElement(Un + "WillShowUI", "OnError"))),
            new XElement(Un + "UserData",
                new XElement(Un + "AcceptEula", true)),
            // tiny11 images have the requirement checks patched out; these
            // LabConfig bypasses make stock Windows 11 ISOs install too.
            new XElement(Un + "RunSynchronous",
                RunSync(1, "reg add HKLM\\SYSTEM\\Setup\\LabConfig /v BypassTPMCheck /t REG_DWORD /d 1 /f"),
                RunSync(2, "reg add HKLM\\SYSTEM\\Setup\\LabConfig /v BypassSecureBootCheck /t REG_DWORD /d 1 /f"),
                RunSync(3, "reg add HKLM\\SYSTEM\\Setup\\LabConfig /v BypassRAMCheck /t REG_DWORD /d 1 /f"),
                RunSync(4, "reg add HKLM\\SYSTEM\\Setup\\LabConfig /v BypassCPUCheck /t REG_DWORD /d 1 /f"),
                RunSync(5, "reg add HKLM\\SYSTEM\\Setup\\LabConfig /v BypassStorageCheck /t REG_DWORD /d 1 /f"),
                // Offline-account escape hatch for OOBE's network requirement.
                RunSync(6, "reg add HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\PasswordLess\\Device /v DevicePasswordLessBuildVersion /t REG_DWORD /d 0 /f"))));

    private static XElement CreatePartition(int order, string type, int? sizeMb = null, bool extend = false)
    {
        var content = new List<object>
        {
            new XElement(Un + "Order", order),
            new XElement(Un + "Type", type),
        };
        if (sizeMb is not null)
        {
            content.Add(new XElement(Un + "Size", sizeMb));
        }

        if (extend)
        {
            content.Add(new XElement(Un + "Extend", true));
        }

        return new XElement(Un + "CreatePartition", new XAttribute(Wcm + "action", "add"), content);
    }

    private static XElement ModifyPartition(int order, int partitionId, string format, string label, char? letter = null)
    {
        var content = new List<object>
        {
            new XElement(Un + "Order", order),
            new XElement(Un + "PartitionID", partitionId),
            new XElement(Un + "Format", format),
            new XElement(Un + "Label", label),
        };
        if (letter is not null)
        {
            content.Add(new XElement(Un + "Letter", letter));
        }

        return new XElement(Un + "ModifyPartition", new XAttribute(Wcm + "action", "add"), content);
    }

    private static XElement RunSync(int order, string command) => new(
        Un + "RunSynchronousCommand", new XAttribute(Wcm + "action", "add"),
        new XElement(Un + "Order", order),
        new XElement(Un + "Path", command));

    private static XElement SpecializePass(string processorArchitecture, string computerName) => Pass("specialize",
        Component("Microsoft-Windows-Shell-Setup", processorArchitecture,
            new XElement(Un + "ComputerName", computerName)));

    private static XElement OobePass(string processorArchitecture, string adminUser, string adminPassword, string provisioningScript)
    {
        // The virtual-FAT unattend stick gets an unpredictable drive letter in
        // the full OS, so scan the usual candidates for the provisioning
        // script. %i is single-percent because this is a direct command line,
        // not a batch file.
        var findStick = $"cmd /c for %i in (C D E F G H I J) do @if exist %i:\\{provisioningScript}" +
            " powershell.exe -NoProfile -ExecutionPolicy Bypass -File %i:\\" + provisioningScript;

        return Pass("oobeSystem",
            Component("Microsoft-Windows-International-Core", processorArchitecture,
                new XElement(Un + "InputLocale", "0409:00000409"),
                new XElement(Un + "SystemLocale", "en-US"),
                new XElement(Un + "UILanguage", "en-US"),
                new XElement(Un + "UserLocale", "en-US")),
            Component("Microsoft-Windows-Shell-Setup", processorArchitecture,
                new XElement(Un + "OOBE",
                    new XElement(Un + "HideEULAPage", true),
                    new XElement(Un + "HideOEMRegistrationScreen", true),
                    new XElement(Un + "HideOnlineAccountScreens", true),
                    new XElement(Un + "HideWirelessSetupInOOBE", true),
                    new XElement(Un + "ProtectYourPC", 3)),
                new XElement(Un + "UserAccounts",
                    new XElement(Un + "LocalAccounts",
                        new XElement(Un + "LocalAccount", new XAttribute(Wcm + "action", "add"),
                            new XElement(Un + "Name", adminUser),
                            new XElement(Un + "DisplayName", "FreshWin Test"),
                            new XElement(Un + "Group", "Administrators"),
                            new XElement(Un + "Password",
                                new XElement(Un + "Value", adminPassword),
                                new XElement(Un + "PlainText", true))))),
                new XElement(Un + "AutoLogon",
                    new XElement(Un + "Enabled", true),
                    new XElement(Un + "Username", adminUser),
                    new XElement(Un + "LogonCount", 5),
                    new XElement(Un + "Password",
                        new XElement(Un + "Value", adminPassword),
                        new XElement(Un + "PlainText", true))),
                new XElement(Un + "FirstLogonCommands",
                    new XElement(Un + "SynchronousCommand", new XAttribute(Wcm + "action", "add"),
                        new XElement(Un + "Order", 1),
                        new XElement(Un + "RequiresUserInput", false),
                        new XElement(Un + "Description", "FreshWin test-image provisioning"),
                        new XElement(Un + "CommandLine", findStick)))));
    }
}
