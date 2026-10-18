// FreshWin (c) Ze'ev Russak — Apache License 2.0 (see LICENSE + NOTICE).
// Derived from ZuTM's AutounattendXmlTests.

using System.Xml.Linq;
using Xunit;

namespace FreshWin.Tests;

public class AutounattendXmlTests
{
    private static readonly XNamespace Un = "urn:schemas-microsoft-com:unattend";

    private static XDocument Generate(GuestArch arch = GuestArch.X64) => XDocument.Parse(
        AutounattendXml.Generate(arch, "freshwin", "fw-pass", "FreshWinVM", "Windows 11 Pro", "freshwin-provision.ps1"));

    [Fact]
    public void Generates_WellFormedXml_WithAllThreePasses()
    {
        var passes = Generate().Root!.Elements(Un + "settings").Select(s => (string)s.Attribute("pass")).ToArray();
        Assert.Contains("windowsPE", passes);
        Assert.Contains("specialize", passes);
        Assert.Contains("oobeSystem", passes);
    }

    [Fact]
    public void ProcessorArchitecture_MatchesGuestArch()
    {
        Assert.All(
            Generate(GuestArch.X64).Root!.Descendants(Un + "component"),
            component => Assert.Equal("amd64", (string)component.Attribute("processorArchitecture")));
        Assert.All(
            Generate(GuestArch.Arm64).Root!.Descendants(Un + "component"),
            component => Assert.Equal("arm64", (string)component.Attribute("processorArchitecture")));
    }

    [Fact]
    public void WindowsPE_ContainsRequirementBypasses()
    {
        var paths = Generate().Root!
            .Descendants(Un + "Path")
            .Select(path => path.Value)
            .ToArray();

        foreach (var key in new[] { "BypassTPMCheck", "BypassSecureBootCheck", "BypassRAMCheck", "BypassCPUCheck", "BypassStorageCheck" })
        {
            Assert.Contains(paths, p => p.Contains(key, StringComparison.Ordinal));
        }

        // Offline-account escape hatch for OOBE's network requirement.
        Assert.Contains(paths, p => p.Contains("DevicePasswordLessBuildVersion", StringComparison.Ordinal));
    }

    [Fact]
    public void WindowsPE_WipesDiskZero_InstallsSelectedImage()
    {
        var root = Generate().Root!;

        Assert.Equal("true", root.Descendants(Un + "WillWipeDisk").First().Value);
        var imageValue = root.Descendants(Un + "MetaData").Descendants(Un + "Value").First().Value;
        Assert.Equal("Windows 11 Pro", imageValue);
        var installTo = root.Descendants(Un + "InstallTo").First();
        Assert.Equal("0", installTo.Element(Un + "DiskID")!.Value);
        Assert.Equal("3", installTo.Element(Un + "PartitionID")!.Value);
    }

    [Fact]
    public void Oobe_CreatesAdminTestUser_WithAutologon()
    {
        var root = Generate().Root!;

        var account = root.Descendants(Un + "LocalAccount").First();
        Assert.Equal("freshwin", account.Element(Un + "Name")!.Value);
        Assert.Equal("Administrators", account.Element(Un + "Group")!.Value);
        Assert.Equal("fw-pass", account.Element(Un + "Password")!.Element(Un + "Value")!.Value);

        var autoLogon = root.Descendants(Un + "AutoLogon").First();
        Assert.Equal("true", autoLogon.Element(Un + "Enabled")!.Value);
        Assert.Equal("freshwin", autoLogon.Element(Un + "Username")!.Value);

        // Skip-the-Microsoft-account screens.
        Assert.Equal("true", root.Descendants(Un + "HideOnlineAccountScreens").First().Value);
        Assert.Equal("3", root.Descendants(Un + "ProtectYourPC").First().Value);
    }

    [Fact]
    public void FirstLogonCommand_FindsProvisionScript_OnAnyStickDrive()
    {
        var command = Generate().Root!
            .Descendants(Un + "SynchronousCommand")
            .First()
            .Element(Un + "CommandLine")!.Value;

        // Single-% because it is a direct command line, not a batch file.
        Assert.Contains("for %i in (C D E F G H I J)", command, StringComparison.Ordinal);
        Assert.Contains("%i:\\freshwin-provision.ps1", command, StringComparison.Ordinal);
        Assert.Contains("-ExecutionPolicy Bypass", command, StringComparison.Ordinal);
    }

    [Fact]
    public void Specialize_SetsComputerName()
    {
        var name = Generate().Root!
            .Elements(Un + "settings")
            .First(s => (string)s.Attribute("pass") == "specialize")
            .Descendants(Un + "ComputerName")
            .Single()
            .Value;
        Assert.Equal("FreshWinVM", name);
    }

    [Fact]
    public void Generate_RejectsMissingInputs()
    {
        Assert.Throws<ArgumentException>(() => AutounattendXml.Generate(GuestArch.X64, "", "pw", "pc", "img", "s.ps1"));
        Assert.Throws<ArgumentException>(() => AutounattendXml.Generate(GuestArch.X64, "u", "", "pc", "img", "s.ps1"));
        Assert.Throws<ArgumentException>(() => AutounattendXml.Generate(GuestArch.X64, "u", "pw", "pc", "", "s.ps1"));
        Assert.Throws<ArgumentException>(() => AutounattendXml.Generate(GuestArch.X64, "u", "pw", "pc", "img", ""));
    }
}
