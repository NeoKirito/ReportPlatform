using PEIS.PrintAgent;
using PEIS.PrintAgent.Printing;
using Xunit;

namespace PEIS.PrintAgent.Tests;

public sealed class CommandPrintBackendTests : IDisposable
{
    private readonly string _temporaryExecutable = Path.GetTempFileName();

    [Fact]
    public void Uses_argument_list_without_shell_and_preserves_untrusted_values_as_single_arguments()
    {
        var info = CommandPrintBackend.BuildStartInfo(
            new PrintBackendOptions
            {
                Executable = _temporaryExecutable,
                ArgumentsTemplate = "--file {file} --printer {printer} --copies {copies} --duplex {duplex}"
            },
            Path.GetFullPath("document with spaces.pdf"),
            "Label Printer & not-a-command",
            2,
            duplex: false);

        Assert.False(info.UseShellExecute);
        Assert.Empty(info.Arguments);
        Assert.Equal(new[]
        {
            "--file", Path.GetFullPath("document with spaces.pdf"),
            "--printer", "Label Printer & not-a-command", "--copies", "2", "--duplex", "false"
        }, info.ArgumentList);
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("powershell.exe")]
    [InlineData("relative-printer.exe")]
    public void Rejects_shell_hosts_and_non_absolute_executables(string executable)
    {
        var candidate = executable.Equals("relative-printer.exe", StringComparison.Ordinal)
            ? executable
            : Path.Combine(Path.GetDirectoryName(_temporaryExecutable)!, executable);
        Assert.Throws<InvalidOperationException>(() => CommandPrintBackend.BuildStartInfo(
            new PrintBackendOptions { Executable = candidate, ArgumentsTemplate = "{file}" },
            Path.GetFullPath("document.pdf"), "Printer", 1, duplex: false));
    }

    [Fact]
    public void Rejects_unknown_or_unbalanced_argument_template_placeholders()
    {
        Assert.Throws<InvalidOperationException>(() => CommandPrintBackend.BuildStartInfo(
            new PrintBackendOptions { Executable = _temporaryExecutable, ArgumentsTemplate = "{unknown}" },
            Path.GetFullPath("document.pdf"), "Printer", 1, duplex: false));
        Assert.Throws<InvalidOperationException>(() => CommandPrintBackend.BuildStartInfo(
            new PrintBackendOptions { Executable = _temporaryExecutable, ArgumentsTemplate = "\"{file}" },
            Path.GetFullPath("document.pdf"), "Printer", 1, duplex: false));
    }

    public void Dispose() => File.Delete(_temporaryExecutable);
}
