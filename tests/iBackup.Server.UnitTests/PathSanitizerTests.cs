using iBackup.Server.Application.Common;
using iBackup.Server.Domain.Exceptions;
using Xunit;

namespace iBackup.Server.UnitTests;

public class PathSanitizerTests
{
    [Theory]
    [InlineData("Documents/report.docx", "Documents/report.docx")]
    [InlineData(@"Documents\report.docx", "Documents/report.docx")]
    [InlineData("a/b/c/d.txt", "a/b/c/d.txt")]
    [InlineData("file.txt", "file.txt")]
    public void NormalizeRelativePath_accepts_valid_paths(string input, string expected)
    {
        Assert.Equal(expected, PathSanitizer.NormalizeRelativePath(input));
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("a/../../b.txt")]
    [InlineData("/rooted/path.txt")]
    [InlineData(@"C:\Windows\system32\config")]
    [InlineData("a//b.txt")]
    [InlineData("dir/./file.txt")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CON/file.txt")]
    [InlineData("dir/NUL.txt")]
    [InlineData("bad<name>.txt")]
    [InlineData("trailingdot./x")]
    public void NormalizeRelativePath_rejects_dangerous_paths(string input)
    {
        Assert.Throws<AppException>(() => PathSanitizer.NormalizeRelativePath(input));
    }

    [Theory]
    [InlineData("report.docx")]
    [InlineData("weird name with spaces.txt")]
    public void ValidateFileName_accepts_valid_names(string name)
    {
        Assert.Equal(name, PathSanitizer.ValidateFileName(name));
    }

    [Theory]
    [InlineData("a/b.txt")]
    [InlineData(@"a\b.txt")]
    [InlineData("..")]
    [InlineData("COM1")]
    [InlineData("")]
    public void ValidateFileName_rejects_invalid_names(string name)
    {
        Assert.Throws<AppException>(() => PathSanitizer.ValidateFileName(name));
    }
}
