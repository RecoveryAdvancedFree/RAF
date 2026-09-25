using RAF.Core.Update;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>קריאת הגרסה האחרונה מתשובת גיטהאב, כמו שבדיקת העדכונים מקבלת אותה.</summary>
public class ReleaseInfoTests
{
    private const string Sha = "dfad2de4bb7eafd8b9c8383a5a78ac0cf9b2b928b6915a60ab7f419a913eb582";

    private static string Json(string tag, bool prerelease = false, string setupDigest = "sha256:" + Sha) => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/RecoveryAdvancedFree/RAF/releases/tag/{{tag}}",
          "draft": false,
          "prerelease": {{(prerelease ? "true" : "false")}},
          "assets": [
            { "name": "RAF-Setup.exe", "size": 20272286, "digest": "{{setupDigest}}",
              "browser_download_url": "https://github.com/RecoveryAdvancedFree/RAF/releases/download/{{tag}}/RAF-Setup.exe" },
            { "name": "RAF.exe", "size": 24884479,
              "browser_download_url": "https://github.com/RecoveryAdvancedFree/RAF/releases/download/{{tag}}/RAF.exe" }
          ]
        }
        """;

    [Fact]
    public void ReadsVersionAndBothFiles()
    {
        var r = ReleaseInfo.Parse(Json("v0.8.0"))!;

        Assert.Equal(new Version(0, 8, 0), r.Version);
        Assert.EndsWith("/v0.8.0/RAF-Setup.exe", r.Setup!.Url);
        Assert.Equal(20272286, r.Setup.Size);
        Assert.Equal(Convert.FromHexString(Sha), r.Setup.Sha256);
        Assert.EndsWith("/v0.8.0/RAF.exe", r.Portable!.Url);
        Assert.Null(r.Portable.Sha256);                          // בלי טביעת אצבע — נבדק רק הגודל
    }

    [Theory]
    [InlineData("0.7.0", true)]
    [InlineData("0.7.9", true)]
    [InlineData("0.8.0", false)]
    [InlineData("0.9.1", false)]
    public void NewerOnlyWhenAhead(string current, bool newer) =>
        Assert.Equal(newer, ReleaseInfo.Parse(Json("v0.8.0"))!.IsNewerThan(Version.Parse(current)));

    [Fact]
    public void AssemblyVersionWithFourPartsCompares()
    {
        // גרסת ההרכבה היא 0.8.0.0 — החלק הרביעי אינו הופך אותה לחדשה או ישנה יותר.
        Assert.False(ReleaseInfo.Parse(Json("v0.8.0"))!.IsNewerThan(new Version(0, 8, 0, 0)));
    }

    [Fact]
    public void BetaIsIgnored()
    {
        Assert.Null(ReleaseInfo.Parse(Json("v0.9.0", prerelease: true)));
        Assert.Null(ReleaseInfo.Parse(Json("v0.6.0-mac-beta")));
    }

    [Fact]
    public void MalformedDigestIsDropped()
    {
        Assert.Null(ReleaseInfo.Parse(Json("v0.8.0", setupDigest: "sha256:zz"))!.Setup!.Sha256);
        Assert.Null(ReleaseInfo.Parse(Json("v0.8.0", setupDigest: "md5:abcd"))!.Setup!.Sha256);
    }
}
