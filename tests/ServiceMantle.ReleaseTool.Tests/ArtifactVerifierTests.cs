using System.IO.Compression;
using System.Text;
using ServiceMantle.ReleaseTool;
using Xunit;

namespace ServiceMantle.ReleaseTool.Tests;

/// <summary>
/// Covers the local artifact verification's controlled failure classification: a corrupt zip, an
/// invalid nuspec XML, duplicate nuspec entries, and duplicate metadata all end as a
/// <see cref="ReleaseToolException"/> - the type the tool's entry point reports with exit code 1 -
/// instead of an unclassified exception escaping to the caller.
/// </summary>
public sealed class ArtifactVerifierTests : IDisposable
{
    private const string Version = "0.1.0-rc.1";
    private const string Commit = "1111111111111111111111111111111111111111";
    private const string Id = "ServiceMantle";

    private readonly string root = Directory
        .CreateTempSubdirectory("servicemantle-artifact-verify")
        .FullName;

    public ArtifactVerifierTests() => Directory.CreateDirectory(Path.Combine(root, "packages"));

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void A_valid_package_and_symbol_pair_passes_verification()
    {
        WritePackage(BuildPackage());

        Verify();
    }

    [Fact]
    public void A_corrupt_zip_is_a_controlled_verification_failure()
    {
        WritePackage([1, 2, 3]);

        var failure = Assert.Throws<ReleaseToolException>(Verify);

        Assert.Equal("A package artifact is not a readable zip archive.", failure.Message);
    }

    [Fact]
    public void A_corrupt_nuspec_xml_is_a_controlled_verification_failure()
    {
        WritePackage(BuildZip(("ServiceMantle.nuspec", "<package><metadata>")));

        var failure = Assert.Throws<ReleaseToolException>(Verify);

        Assert.Equal("A package artifact contains a nuspec that is not valid XML.", failure.Message);
    }

    [Fact]
    public void Duplicate_nuspec_entries_are_a_controlled_verification_failure()
    {
        var nuspec = BuildNuspec();
        WritePackage(BuildZip(
            ("ServiceMantle.nuspec", nuspec),
            ("extra.nuspec", nuspec)));

        var failure = Assert.Throws<ReleaseToolException>(Verify);

        Assert.Equal("A package artifact does not contain exactly one nuspec.", failure.Message);
    }

    [Fact]
    public void Duplicate_repository_metadata_is_a_controlled_verification_failure()
    {
        var nuspec = BuildNuspec(repositoryCount: 2);
        WritePackage(BuildZip(("ServiceMantle.nuspec", nuspec)));

        var failure = Assert.Throws<ReleaseToolException>(Verify);

        Assert.Equal("A package contains duplicate repository metadata.", failure.Message);
    }

    [Fact]
    public void Duplicate_metadata_elements_are_a_controlled_verification_failure()
    {
        var nuspec = BuildNuspec(licenseCount: 2);
        WritePackage(BuildZip(("ServiceMantle.nuspec", nuspec)));

        var failure = Assert.Throws<ReleaseToolException>(Verify);

        Assert.Equal("A package contains incorrect license metadata.", failure.Message);
    }

    [Fact]
    public void A_missing_repository_stays_the_existing_verification_failure()
    {
        var nuspec = BuildNuspec(repositoryCount: 0);
        WritePackage(BuildZip(("ServiceMantle.nuspec", nuspec)));

        var failure = Assert.Throws<ReleaseToolException>(Verify);

        Assert.Equal("A package is missing repository metadata.", failure.Message);
    }

    [Fact]
    public void A_wrong_repository_commit_stays_the_existing_verification_failure()
    {
        var nuspec = BuildNuspec(commit: "9999999999999999999999999999999999999999");
        WritePackage(BuildZip(("ServiceMantle.nuspec", nuspec)));

        var failure = Assert.Throws<ReleaseToolException>(Verify);

        Assert.Equal("A package has incorrect repository metadata.", failure.Message);
    }

    [Fact]
    public void A_missing_nuspec_stays_the_existing_verification_failure()
    {
        WritePackage(BuildZip(("readme.txt", "no nuspec here")));

        var failure = Assert.Throws<ReleaseToolException>(Verify);

        Assert.Equal("A package artifact does not contain exactly one nuspec.", failure.Message);
    }

    private void Verify() => ArtifactVerifier.Verify(
        root,
        new PackageRegistry
        {
            SchemaVersion = 1,
            Packages = [new RegisteredPackage { Id = Id, Project = $"src/{Id}/{Id}.csproj" }],
        },
        Version,
        Commit,
        "packages");

    private void WritePackage(byte[] nupkg)
    {
        File.WriteAllBytes(Path.Combine(root, "packages", $"{Id}.{Version}.nupkg"), nupkg);
        File.WriteAllBytes(Path.Combine(root, "packages", $"{Id}.{Version}.snupkg"), nupkg);
    }

    private static byte[] BuildPackage(string? commit = Commit) =>
        BuildZip(("ServiceMantle.nuspec", BuildNuspec(commit)));

    private static string BuildNuspec(string? commit = Commit, int repositoryCount = 1, int licenseCount = 1)
    {
        var repository = commit is null
            ? "<repository type=\"git\" url=\"https://github.com/philfanzhou/ServiceMantle\" />"
            : $"<repository type=\"git\" url=\"https://github.com/philfanzhou/ServiceMantle\" commit=\"{commit}\" />";
        return
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<package><metadata>" +
            $"<id>{Id}</id><version>{Version}</version>" +
            string.Concat(Enumerable.Repeat("<license type=\"expression\">MIT</license>", licenseCount)) +
            string.Concat(Enumerable.Repeat(repository, repositoryCount)) +
            "</metadata></package>";
    }

    private static byte[] BuildZip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return buffer.ToArray();
    }
}
