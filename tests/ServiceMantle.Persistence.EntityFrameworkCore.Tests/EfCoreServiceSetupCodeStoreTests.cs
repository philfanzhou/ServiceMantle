using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ServiceMantle.Installation;
using Xunit;

namespace ServiceMantle.Persistence.EntityFrameworkCore.Tests;

public sealed class EfCoreServiceSetupCodeStoreTests
{
    private static readonly ServiceId Service = ServiceId.Parse("signacore");
    private static readonly DateTime CreatedAtUtc = new(2026, 01, 01, 00, 00, 00, DateTimeKind.Utc);
    private static readonly DateTimeOffset Now = new(2026, 01, 01, 01, 00, 00, TimeSpan.Zero);
    private const string ValidDigest =
        "sha256-v1:653bb1245e828fcda4fa53fcd5a3def5bd7654e651f54b4132b73d74e64435c4";

    [Fact]
    public async Task ModelBuilder_maps_the_setup_code_columns()
    {
        await using var harness = await Harness.CreateAsync();
        var entityType = harness.Context.Model.FindEntityType(typeof(ServiceInstallationEntity))!;

        Assert.Equal(
            "setup_code_generation",
            entityType.FindProperty(nameof(ServiceInstallationEntity.SetupCodeGeneration))!.GetColumnName());
        Assert.False(entityType
            .FindProperty(nameof(ServiceInstallationEntity.SetupCodeGeneration))!.IsNullable);
        Assert.Equal(
            "setup_code_digest",
            entityType.FindProperty(nameof(ServiceInstallationEntity.SetupCodeDigest))!.GetColumnName());
        Assert.Equal(
            SetupCodeDigest.ValueLength,
            entityType.FindProperty(nameof(ServiceInstallationEntity.SetupCodeDigest))!.GetMaxLength());
        Assert.True(entityType
            .FindProperty(nameof(ServiceInstallationEntity.SetupCodeDigest))!.IsNullable);
        Assert.Equal(
            "setup_code_issued_at_utc",
            entityType.FindProperty(nameof(ServiceInstallationEntity.SetupCodeIssuedAtUtc))!.GetColumnName());
        Assert.Equal(
            "setup_code_expires_at_utc",
            entityType.FindProperty(nameof(ServiceInstallationEntity.SetupCodeExpiresAtUtc))!.GetColumnName());
    }

    [Fact]
    public async Task CreatePendingAsync_initializes_generation_zero_and_empty_material()
    {
        await using var harness = await Harness.CreateAsync(seed: false);
        var installationStore = new EfCoreServiceInstallationStore<SetupCodeDbContext>(
            harness.Context,
            harness.Time);

        await installationStore.CreatePendingAsync(Service, TestContext.Current.CancellationToken);

        var row = await harness.ReadAsync();
        Assert.Equal(0, row.SetupCodeGeneration);
        Assert.Null(row.SetupCodeDigest);
        Assert.Null(row.SetupCodeIssuedAtUtc);
        Assert.Null(row.SetupCodeExpiresAtUtc);
    }

    [Fact]
    public async Task CreateAsync_issues_the_first_code_saves_it_and_never_persists_the_plaintext()
    {
        await using var harness = await Harness.CreateAsync();
        var store = harness.Store();

        var result = await store.CreateAsync(Service, TestContext.Current.CancellationToken);

        Assert.True(result.IsIssued);
        Assert.Equal(1, result.Generation);
        Assert.Equal(Now.UtcDateTime.AddMinutes(30), result.ExpiresAtUtc);
        Assert.Null(result.ErrorCode);

        var plaintext = result.SetupCode!.Reveal();
        var row = await harness.ReadAsync();
        Assert.Equal(1, row.SetupCodeGeneration);
        Assert.Equal(SetupCodeDigest.Compute(result.SetupCode!).Value, row.SetupCodeDigest);
        Assert.Equal(Now.UtcDateTime, row.SetupCodeIssuedAtUtc);
        Assert.Equal(Now.UtcDateTime.AddMinutes(30), row.SetupCodeExpiresAtUtc);
        Assert.Equal(2, row.Version);
        Assert.DoesNotContain(plaintext, row.SetupCodeDigest!, StringComparison.Ordinal);
        Assert.DoesNotContain(plaintext, row.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(plaintext, result.ToString(), StringComparison.Ordinal);
        Assert.True((await store.ValidateAsync(
            Service,
            plaintext,
            TestContext.Current.CancellationToken)).IsValid);
    }

    [Fact]
    public async Task CreateAsync_rejects_a_second_creation()
    {
        await using var harness = await Harness.CreateAsync();
        var store = harness.Store();
        await store.CreateAsync(Service, TestContext.Current.CancellationToken);

        var second = await store.CreateAsync(Service, TestContext.Current.CancellationToken);

        Assert.False(second.IsIssued);
        Assert.Null(second.SetupCode);
        Assert.Equal(WellKnownSetupCodeErrorCodes.AlreadyExists, second.ErrorCode);
        Assert.Equal(1, (await harness.ReadAsync()).SetupCodeGeneration);
    }

    [Fact]
    public async Task RotateAsync_replaces_the_material_and_invalidates_the_previous_code()
    {
        await using var harness = await Harness.CreateAsync();
        var store = harness.Store();
        var first = await store.CreateAsync(Service, TestContext.Current.CancellationToken);

        var rotated = await store.RotateAsync(Service, TestContext.Current.CancellationToken);

        Assert.True(rotated.IsIssued);
        Assert.Equal(2, rotated.Generation);
        Assert.NotEqual(first.SetupCode!.Reveal(), rotated.SetupCode!.Reveal());
        Assert.Equal(
            WellKnownSetupCodeErrorCodes.Invalid,
            (await store.ValidateAsync(
                Service,
                first.SetupCode!.Reveal(),
                TestContext.Current.CancellationToken)).ErrorCode);
        Assert.True((await store.ValidateAsync(
            Service,
            rotated.SetupCode!.Reveal(),
            TestContext.Current.CancellationToken)).IsValid);
        Assert.Equal(3, (await harness.ReadAsync()).Version);
    }

    [Fact]
    public async Task RotateAsync_rejects_a_never_issued_installation()
    {
        await using var harness = await Harness.CreateAsync();

        var result = await harness.Store().RotateAsync(Service, TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownSetupCodeErrorCodes.NotCreated, result.ErrorCode);
        Assert.Null(result.SetupCode);
        Assert.Equal(0, (await harness.ReadAsync()).SetupCodeGeneration);
    }

    [Fact]
    public async Task RotateAsync_replaces_expired_material_but_rejects_an_exhausted_generation()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.SetMaterialAsync(1, ValidDigest, CreatedAtUtc, CreatedAtUtc.AddMinutes(5));
        var store = harness.Store();

        var rotated = await store.RotateAsync(Service, TestContext.Current.CancellationToken);
        Assert.True(rotated.IsIssued);
        Assert.Equal(2, rotated.Generation);

        await harness.SetMaterialAsync(
            int.MaxValue,
            ValidDigest,
            CreatedAtUtc,
            CreatedAtUtc.AddMinutes(5));
        var exhausted = await store.RotateAsync(Service, TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownSetupCodeErrorCodes.GenerationExhausted, exhausted.ErrorCode);
        Assert.Null(exhausted.SetupCode);
        Assert.Equal(int.MaxValue, (await harness.ReadAsync()).SetupCodeGeneration);
        Assert.Equal(ValidDigest, (await harness.ReadAsync()).SetupCodeDigest);
    }

    [Theory]
    [InlineData("digest-missing")]
    [InlineData("issued-missing")]
    [InlineData("expires-missing")]
    [InlineData("all-missing")]
    [InlineData("generation-zero-with-material")]
    [InlineData("unknown-digest-version")]
    [InlineData("malformed-digest")]
    [InlineData("inverted-times")]
    [InlineData("issued-before-creation")]
    public async Task Corrupt_material_fails_closed_for_every_operation(string corruption)
    {
        await using var harness = await Harness.CreateAsync();
        await harness.SetMaterialAsync(corruption switch
        {
            "generation-zero-with-material" => 0,
            _ => 1,
        },
        corruption switch
        {
            "digest-missing" or "all-missing" => null,
            "unknown-digest-version" => "sha256-v2:" + ValidDigest[SetupCodeDigest.Prefix.Length..],
            "malformed-digest" => "sha256-v1:not-a-digest",
            _ => ValidDigest,
        },
        corruption switch
        {
            "issued-missing" or "all-missing" => null,
            "inverted-times" => CreatedAtUtc.AddMinutes(30),
            "issued-before-creation" => CreatedAtUtc.AddMinutes(-1),
            _ => CreatedAtUtc,
        },
        corruption switch
        {
            "expires-missing" or "all-missing" => null,
            "inverted-times" => CreatedAtUtc.AddMinutes(10),
            _ => CreatedAtUtc.AddMinutes(30),
        });

        var store = harness.Store();
        var candidate = new string('a', SetupCode.Length);

        Assert.Equal(
            WellKnownSetupCodeErrorCodes.StorageCorrupt,
            (await store.CreateAsync(Service, TestContext.Current.CancellationToken)).ErrorCode);
        Assert.Equal(
            WellKnownSetupCodeErrorCodes.StorageCorrupt,
            (await store.RotateAsync(Service, TestContext.Current.CancellationToken)).ErrorCode);
        Assert.Equal(
            WellKnownSetupCodeErrorCodes.StorageCorrupt,
            (await store.ValidateAsync(Service, candidate, TestContext.Current.CancellationToken)).ErrorCode);
        Assert.Equal(
            WellKnownSetupCodeErrorCodes.StorageCorrupt,
            (await store.StageConsumeAsync(Service, candidate, TestContext.Current.CancellationToken)).ErrorCode);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("issued")]
    [InlineData("expires")]
    public async Task Deleting_any_material_field_never_reopens_creation(string field)
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Store().CreateAsync(Service, TestContext.Current.CancellationToken);
        await harness.MutateAsync(row =>
        {
            switch (field)
            {
                case "digest":
                    row.SetupCodeDigest = null;
                    break;
                case "issued":
                    row.SetupCodeIssuedAtUtc = null;
                    break;
                default:
                    row.SetupCodeExpiresAtUtc = null;
                    break;
            }
        });

        var result = await harness.Store().CreateAsync(Service, TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownSetupCodeErrorCodes.StorageCorrupt, result.ErrorCode);
        Assert.Null(result.SetupCode);
    }

    [Fact]
    public async Task Missing_installation_is_reported_for_every_operation()
    {
        await using var harness = await Harness.CreateAsync(seed: false);
        var store = harness.Store();
        var candidate = new string('a', SetupCode.Length);

        Assert.Equal(
            WellKnownSetupCodeErrorCodes.InstallationNotFound,
            (await store.CreateAsync(Service, TestContext.Current.CancellationToken)).ErrorCode);
        Assert.Equal(
            WellKnownSetupCodeErrorCodes.InstallationNotFound,
            (await store.RotateAsync(Service, TestContext.Current.CancellationToken)).ErrorCode);
        Assert.Equal(
            WellKnownSetupCodeErrorCodes.InstallationNotFound,
            (await store.ValidateAsync(Service, candidate, TestContext.Current.CancellationToken)).ErrorCode);
        Assert.Equal(
            WellKnownSetupCodeErrorCodes.InstallationNotFound,
            (await store.StageConsumeAsync(Service, candidate, TestContext.Current.CancellationToken)).ErrorCode);
    }

    [Fact]
    public async Task Completed_installation_never_creates_restores_or_validates_a_code()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.MutateAsync(row =>
        {
            row.Status = InstallationStatus.Completed;
            row.CompletedAtUtc = CreatedAtUtc.AddMinutes(10);
            row.SetupCodeGeneration = 3;
        });

        var store = harness.Store();

        Assert.Equal(
            WellKnownSetupCodeErrorCodes.InstallationCompleted,
            (await store.CreateAsync(Service, TestContext.Current.CancellationToken)).ErrorCode);
        Assert.Equal(
            WellKnownSetupCodeErrorCodes.InstallationCompleted,
            (await store.RotateAsync(Service, TestContext.Current.CancellationToken)).ErrorCode);
        // A malformed candidate must not turn a completed installation into an "invalid code" answer.
        Assert.Equal(
            WellKnownSetupCodeErrorCodes.InstallationCompleted,
            (await store.ValidateAsync(Service, "not-a-code", TestContext.Current.CancellationToken)).ErrorCode);
        Assert.Equal(
            WellKnownSetupCodeErrorCodes.InstallationCompleted,
            (await store.StageConsumeAsync(Service, "not-a-code", TestContext.Current.CancellationToken)).ErrorCode);
        Assert.Equal(3, (await harness.ReadAsync()).SetupCodeGeneration);
    }

    [Fact]
    public async Task ValidateAsync_is_read_only_and_rejects_malformed_and_mismatched_candidates()
    {
        await using var harness = await Harness.CreateAsync();
        var store = harness.Store();
        var issued = await store.CreateAsync(Service, TestContext.Current.CancellationToken);
        var before = await harness.ReadAsync();
        harness.Context.ChangeTracker.Clear();

        Assert.True((await store.ValidateAsync(
            Service,
            issued.SetupCode!.Reveal(),
            TestContext.Current.CancellationToken)).IsValid);
        Assert.Equal(
            WellKnownSetupCodeErrorCodes.Invalid,
            (await store.ValidateAsync(Service, "short", TestContext.Current.CancellationToken)).ErrorCode);
        Assert.Equal(
            WellKnownSetupCodeErrorCodes.Invalid,
            (await store.ValidateAsync(
                Service,
                new string('a', SetupCode.Length),
                TestContext.Current.CancellationToken)).ErrorCode);

        Assert.Empty(harness.Context.ChangeTracker.Entries<ServiceInstallationEntity>());
        var after = await harness.ReadAsync();
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.SetupCodeGeneration, after.SetupCodeGeneration);
        Assert.Equal(before.SetupCodeDigest, after.SetupCodeDigest);
    }

    [Fact]
    public async Task Expired_material_reports_expiry_even_when_the_candidate_does_not_match()
    {
        await using var harness = await Harness.CreateAsync();
        var issued = await harness.Store().CreateAsync(Service, TestContext.Current.CancellationToken);
        harness.Time.UtcNow = Now.AddMinutes(30);
        var store = harness.Store();

        Assert.Equal(
            WellKnownSetupCodeErrorCodes.Expired,
            (await store.ValidateAsync(
                Service,
                issued.SetupCode!.Reveal(),
                TestContext.Current.CancellationToken)).ErrorCode);
        Assert.Equal(
            WellKnownSetupCodeErrorCodes.Expired,
            (await store.ValidateAsync(
                Service,
                new string('a', SetupCode.Length),
                TestContext.Current.CancellationToken)).ErrorCode);
        Assert.Equal(
            WellKnownSetupCodeErrorCodes.Expired,
            (await store.StageConsumeAsync(
                Service,
                issued.SetupCode!.Reveal(),
                TestContext.Current.CancellationToken)).ErrorCode);
        // A malformed candidate is still rejected on format before expiry is considered.
        Assert.Equal(
            WellKnownSetupCodeErrorCodes.Invalid,
            (await store.ValidateAsync(Service, "short", TestContext.Current.CancellationToken)).ErrorCode);
    }

    [Fact]
    public async Task StageConsumeAsync_stages_completion_without_saving()
    {
        await using var harness = await Harness.CreateAsync();
        var store = harness.Store();
        var issued = await store.CreateAsync(Service, TestContext.Current.CancellationToken);

        var staged = await store.StageConsumeAsync(
            Service,
            issued.SetupCode!.Reveal(),
            TestContext.Current.CancellationToken);

        Assert.True(staged.IsStaged);
        Assert.True(staged.State!.IsCompleted);

        var beforeSave = await harness.ReadAsync();
        Assert.Equal(InstallationStatus.PendingSetup, beforeSave.Status);
        Assert.Equal(2, beforeSave.Version);
        Assert.NotNull(beforeSave.SetupCodeDigest);

        await harness.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var afterSave = await harness.ReadAsync();
        Assert.Equal(InstallationStatus.Completed, afterSave.Status);
        Assert.Equal(Now.UtcDateTime, afterSave.CompletedAtUtc);
        Assert.Equal(3, afterSave.Version);
        Assert.Null(afterSave.SetupCodeDigest);
        Assert.Null(afterSave.SetupCodeIssuedAtUtc);
        Assert.Null(afterSave.SetupCodeExpiresAtUtc);
        // The generation is retained as a non-sensitive issuance history marker.
        Assert.Equal(1, afterSave.SetupCodeGeneration);
    }

    [Fact]
    public async Task StageConsumeAsync_rollback_leaves_the_installation_pending_and_the_code_valid()
    {
        await using var harness = await Harness.CreateAsync();
        var store = harness.Store();
        var issued = await store.CreateAsync(Service, TestContext.Current.CancellationToken);
        var plaintext = issued.SetupCode!.Reveal();

        await using (var transaction = await harness.Context.Database.BeginTransactionAsync(
            TestContext.Current.CancellationToken))
        {
            var staged = await store.StageConsumeAsync(
                Service,
                plaintext,
                TestContext.Current.CancellationToken);
            Assert.True(staged.IsStaged);

            await harness.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        // The stale tracker still shows the staged completion, which is exactly why it must not be
        // used as the installation authority until it is reloaded, restored, or detached.
        Assert.Equal(
            InstallationStatus.Completed,
            harness.Context.ChangeTracker
                .Entries<ServiceInstallationEntity>()
                .Single()
                .Entity
                .Status);

        await using var freshContext = harness.NewContext();
        var freshRow = await freshContext.ServiceInstallations
            .AsNoTracking()
            .SingleAsync(item => item.ServiceId == Service.Value, TestContext.Current.CancellationToken);
        Assert.Equal(InstallationStatus.PendingSetup, freshRow.Status);
        Assert.Null(freshRow.CompletedAtUtc);

        var freshStore = new EfCoreServiceSetupCodeStore<SetupCodeDbContext>(freshContext, harness.Time);
        Assert.True((await freshStore.ValidateAsync(
            Service,
            plaintext,
            TestContext.Current.CancellationToken)).IsValid);
    }

    [Fact]
    public async Task Create_and_rotate_refuse_any_pre_existing_dirty_entry()
    {
        await using var harness = await Harness.CreateAsync();
        var store = harness.Store();
        harness.Context.ServiceInstallations.Add(new ServiceInstallationEntity
        {
            ServiceId = "unrelated",
            Status = InstallationStatus.PendingSetup,
            CreatedAtUtc = CreatedAtUtc,
            Version = 1,
        });

        Assert.Equal(
            WellKnownSetupCodeErrorCodes.DirtyContext,
            (await store.CreateAsync(Service, TestContext.Current.CancellationToken)).ErrorCode);
        Assert.Equal(
            WellKnownSetupCodeErrorCodes.DirtyContext,
            (await store.RotateAsync(Service, TestContext.Current.CancellationToken)).ErrorCode);

        // Nothing was generated and nothing was saved, so the caller's own pending insert survives.
        var row = await harness.ReadAsync();
        Assert.Equal(0, row.SetupCodeGeneration);
        Assert.Null(row.SetupCodeDigest);
        Assert.Contains(
            harness.Context.ChangeTracker.Entries<ServiceInstallationEntity>(),
            entry => entry.State == EntityState.Added && entry.Entity.ServiceId == "unrelated");
    }

    [Fact]
    public async Task Create_tolerates_unrelated_unchanged_entries()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.SeedAsync("unrelated");
        harness.Context.ChangeTracker.Clear();
        var unrelated = await harness.Context.ServiceInstallations.SingleAsync(
            item => item.ServiceId == "unrelated",
            TestContext.Current.CancellationToken);

        var result = await harness.Store().CreateAsync(Service, TestContext.Current.CancellationToken);

        Assert.True(result.IsIssued);
        Assert.Equal(
            EntityState.Unchanged,
            harness.Context.Entry(unrelated).State);
        Assert.Equal(1, (await harness.ReadAsync("unrelated")).Version);
    }

    [Fact]
    public async Task StageConsume_allows_unrelated_dirty_entries_but_refuses_a_dirty_target()
    {
        await using var harness = await Harness.CreateAsync();
        var store = harness.Store();
        var issued = await store.CreateAsync(Service, TestContext.Current.CancellationToken);
        harness.Context.ChangeTracker.Clear();

        harness.Context.ServiceInstallations.Add(new ServiceInstallationEntity
        {
            ServiceId = "unrelated",
            Status = InstallationStatus.PendingSetup,
            CreatedAtUtc = CreatedAtUtc,
            Version = 1,
        });

        var staged = await store.StageConsumeAsync(
            Service,
            issued.SetupCode!.Reveal(),
            TestContext.Current.CancellationToken);
        Assert.True(staged.IsStaged);

        var dirtyTarget = await store.StageConsumeAsync(
            Service,
            issued.SetupCode!.Reveal(),
            TestContext.Current.CancellationToken);
        Assert.Equal(WellKnownSetupCodeErrorCodes.DirtyContext, dirtyTarget.ErrorCode);
    }

    [Fact]
    public async Task Create_and_rotate_refuse_a_dirty_entry_with_change_detection_disabled()
    {
        // ChangeTracker.Entries() reports the last detected state. With AutoDetectChangesEnabled off
        // a caller's modification stays reported as Unchanged, so without an explicit detection the
        // clean-context precondition would pass and this operation's own SaveChanges would commit
        // the caller's pending change along with a freshly generated code.
        await using var harness = await Harness.CreateAsync();
        await harness.SeedAsync("unrelated");
        harness.Context.ChangeTracker.Clear();
        harness.Context.ChangeTracker.AutoDetectChangesEnabled = false;

        var unrelated = await harness.Context.ServiceInstallations.SingleAsync(
            item => item.ServiceId == "unrelated",
            TestContext.Current.CancellationToken);
        unrelated.CreatedAtUtc = CreatedAtUtc.AddMinutes(1);

        var store = harness.Store();
        Assert.Equal(
            WellKnownSetupCodeErrorCodes.DirtyContext,
            (await store.CreateAsync(Service, TestContext.Current.CancellationToken)).ErrorCode);
        Assert.Equal(
            WellKnownSetupCodeErrorCodes.DirtyContext,
            (await store.RotateAsync(Service, TestContext.Current.CancellationToken)).ErrorCode);

        // Nothing was generated, nothing was saved, and the caller's setting is untouched.
        Assert.False(harness.Context.ChangeTracker.AutoDetectChangesEnabled);
        var row = await harness.ReadAsync();
        Assert.Equal(0, row.SetupCodeGeneration);
        Assert.Null(row.SetupCodeDigest);
        Assert.Equal(CreatedAtUtc, (await harness.ReadAsync("unrelated")).CreatedAtUtc);
    }

    [Fact]
    public async Task StageConsume_refuses_a_dirty_target_with_change_detection_disabled()
    {
        await using var harness = await Harness.CreateAsync();
        var store = harness.Store();
        var issued = await store.CreateAsync(Service, TestContext.Current.CancellationToken);
        harness.Context.ChangeTracker.Clear();
        harness.Context.ChangeTracker.AutoDetectChangesEnabled = false;

        var target = await harness.Context.ServiceInstallations.SingleAsync(
            item => item.ServiceId == Service.Value,
            TestContext.Current.CancellationToken);
        target.CreatedAtUtc = CreatedAtUtc.AddMinutes(1);

        var result = await store.StageConsumeAsync(
            Service,
            issued.SetupCode!.Reveal(),
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownSetupCodeErrorCodes.DirtyContext, result.ErrorCode);
        Assert.False(harness.Context.ChangeTracker.AutoDetectChangesEnabled);
        Assert.Equal(InstallationStatus.PendingSetup, (await harness.ReadAsync()).Status);
    }

    [Fact]
    public async Task StageConsume_still_allows_an_unrelated_dirty_entry_with_change_detection_disabled()
    {
        // Detecting changes must not turn the tolerated unrelated-dirty case into a refusal.
        await using var harness = await Harness.CreateAsync();
        await harness.SeedAsync("unrelated");
        var store = harness.Store();
        var issued = await store.CreateAsync(Service, TestContext.Current.CancellationToken);
        harness.Context.ChangeTracker.Clear();
        harness.Context.ChangeTracker.AutoDetectChangesEnabled = false;

        var unrelated = await harness.Context.ServiceInstallations.SingleAsync(
            item => item.ServiceId == "unrelated",
            TestContext.Current.CancellationToken);
        unrelated.CreatedAtUtc = CreatedAtUtc.AddMinutes(1);

        var result = await store.StageConsumeAsync(
            Service,
            issued.SetupCode!.Reveal(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsStaged);
        Assert.False(harness.Context.ChangeTracker.AutoDetectChangesEnabled);
    }

    [Fact]
    public async Task Create_and_rotate_persist_their_writes_with_change_detection_disabled()
    {
        // Writing to a tracked POCO is only picked up by change detection. With
        // AutoDetectChangesEnabled off, an operation that mutates the entity and then saves would
        // commit nothing while still returning the plaintext, so a caller would hold a code that
        // does not exist in the database.
        await using var harness = await Harness.CreateAsync();
        harness.Context.ChangeTracker.AutoDetectChangesEnabled = false;
        var store = harness.Store();

        var created = await store.CreateAsync(Service, TestContext.Current.CancellationToken);

        Assert.True(created.IsIssued);
        var afterCreate = await harness.ReadAsync();
        Assert.Equal(1, afterCreate.SetupCodeGeneration);
        Assert.Equal(
            SetupCodeDigest.Compute(created.SetupCode!).Value,
            afterCreate.SetupCodeDigest);
        Assert.Equal(2, afterCreate.Version);

        var rotated = await store.RotateAsync(Service, TestContext.Current.CancellationToken);

        Assert.True(rotated.IsIssued);
        var afterRotate = await harness.ReadAsync();
        Assert.Equal(2, afterRotate.SetupCodeGeneration);
        Assert.Equal(
            SetupCodeDigest.Compute(rotated.SetupCode!).Value,
            afterRotate.SetupCodeDigest);
        Assert.Equal(3, afterRotate.Version);

        // The caller's own setting is never changed on their behalf.
        Assert.False(harness.Context.ChangeTracker.AutoDetectChangesEnabled);
    }

    [Fact]
    public async Task StageConsume_stages_its_writes_with_change_detection_disabled()
    {
        // The staged completion must reach the caller's SaveChanges even when the caller turned
        // automatic change detection off; otherwise StageConsume reports a consumed code while the
        // row stays PendingSetup with its digest intact.
        await using var harness = await Harness.CreateAsync();
        var store = harness.Store();
        var issued = await store.CreateAsync(Service, TestContext.Current.CancellationToken);
        harness.Context.ChangeTracker.Clear();
        harness.Context.ChangeTracker.AutoDetectChangesEnabled = false;

        var staged = await store.StageConsumeAsync(
            Service,
            issued.SetupCode!.Reveal(),
            TestContext.Current.CancellationToken);

        Assert.True(staged.IsStaged);
        await harness.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var row = await harness.ReadAsync();
        Assert.Equal(InstallationStatus.Completed, row.Status);
        Assert.Null(row.SetupCodeDigest);
        Assert.Null(row.SetupCodeIssuedAtUtc);
        Assert.Null(row.SetupCodeExpiresAtUtc);
        Assert.Equal(Now.UtcDateTime, row.CompletedAtUtc);
        Assert.Equal(3, row.Version);
        Assert.False(harness.Context.ChangeTracker.AutoDetectChangesEnabled);
    }

    [Theory]
    [InlineData("create-vs-create")]
    [InlineData("rotate-vs-rotate")]
    [InlineData("create-vs-rotate")]
    public async Task Only_one_writer_on_a_baseline_version_succeeds(string race)
    {
        await using var harness = await Harness.CreateAsync();
        await using var otherContext = harness.NewContext();
        var store = harness.Store();
        var otherStore = new EfCoreServiceSetupCodeStore<SetupCodeDbContext>(otherContext, harness.Time);

        if (race == "rotate-vs-rotate")
        {
            await store.CreateAsync(Service, TestContext.Current.CancellationToken);
            harness.Context.ChangeTracker.Clear();
        }

        SetupCodeIssueResult? interference = null;
        harness.Context.BeforeSaveChangesAsync = async cancellationToken =>
        {
            interference = race == "rotate-vs-rotate"
                ? await otherStore.RotateAsync(Service, cancellationToken)
                : await otherStore.CreateAsync(Service, cancellationToken);
            if (race == "create-vs-rotate")
            {
                // The competing context commits a Create and then a Rotate before this operation's
                // own save reaches the database on the baseline it loaded.
                interference = await otherStore.RotateAsync(Service, cancellationToken);
            }
        };

        var result = race == "rotate-vs-rotate"
            ? await store.RotateAsync(Service, TestContext.Current.CancellationToken)
            : await store.CreateAsync(Service, TestContext.Current.CancellationToken);

        Assert.NotNull(interference);
        Assert.True(interference!.IsIssued);
        Assert.False(result.IsIssued);
        Assert.Null(result.SetupCode);
        Assert.Equal(WellKnownSetupCodeErrorCodes.ConcurrencyConflict, result.ErrorCode);

        var row = await harness.ReadAsync();
        Assert.Equal(
            SetupCodeDigest.Compute(interference.SetupCode!).Value,
            row.SetupCodeDigest);
    }

    [Fact]
    public async Task Save_failure_restores_only_the_entry_this_operation_touched()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.SeedAsync("unrelated");
        harness.Context.ChangeTracker.Clear();

        var target = await harness.Context.ServiceInstallations.SingleAsync(
            item => item.ServiceId == Service.Value,
            TestContext.Current.CancellationToken);
        var unrelated = await harness.Context.ServiceInstallations.SingleAsync(
            item => item.ServiceId == "unrelated",
            TestContext.Current.CancellationToken);
        harness.Context.BeforeSaveChangesAsync = _ =>
            throw new InvalidOperationException("provider failure");

        var exception = await Assert.ThrowsAsync<ServiceInstallationStoreException>(() =>
            harness.Store().CreateAsync(Service, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("installation.storage_error", exception.ErrorCode);
        Assert.DoesNotContain("provider failure", exception.ToString(), StringComparison.Ordinal);

        // The pre-tracked target entry is restored in place, never detached or cleared.
        Assert.Equal(EntityState.Unchanged, harness.Context.Entry(target).State);
        Assert.Equal(0, target.SetupCodeGeneration);
        Assert.Null(target.SetupCodeDigest);
        Assert.Equal(1, target.Version);
        Assert.Equal(EntityState.Unchanged, harness.Context.Entry(unrelated).State);
        Assert.Equal(2, harness.Context.ChangeTracker.Entries<ServiceInstallationEntity>().Count());

        var row = await harness.ReadAsync();
        Assert.Equal(0, row.SetupCodeGeneration);
        Assert.Equal(1, row.Version);
    }

    [Fact]
    public async Task Concurrency_conflict_detaches_an_entry_this_operation_loaded()
    {
        await using var harness = await Harness.CreateAsync();
        await using var otherContext = harness.NewContext();
        var otherStore = new EfCoreServiceSetupCodeStore<SetupCodeDbContext>(otherContext, harness.Time);
        harness.Context.BeforeSaveChangesAsync = async cancellationToken =>
            await otherStore.CreateAsync(Service, cancellationToken);

        var result = await harness.Store().CreateAsync(Service, TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownSetupCodeErrorCodes.ConcurrencyConflict, result.ErrorCode);
        Assert.Empty(harness.Context.ChangeTracker.Entries<ServiceInstallationEntity>());
    }

    [Fact]
    public async Task Version_overflow_and_clock_inversion_fail_closed()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.MutateAsync(row => row.Version = int.MaxValue);

        Assert.Equal(
            WellKnownSetupCodeErrorCodes.StateInvariantViolation,
            (await harness.Store().CreateAsync(Service, TestContext.Current.CancellationToken)).ErrorCode);

        await harness.MutateAsync(row => row.Version = 1);
        harness.Time.UtcNow = new DateTimeOffset(CreatedAtUtc.AddMinutes(-1), TimeSpan.Zero);

        Assert.Equal(
            WellKnownSetupCodeErrorCodes.StorageCorrupt,
            (await harness.Store().CreateAsync(Service, TestContext.Current.CancellationToken)).ErrorCode);
    }

    [Fact]
    public async Task Programming_errors_and_caller_cancellation_use_the_exception_channel()
    {
        await using var harness = await Harness.CreateAsync();
        var store = harness.Store();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            store.CreateAsync(null!, TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            store.ValidateAsync(Service, null!, TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            store.StageConsumeAsync(Service, null!, TestContext.Current.CancellationToken).AsTask());
        Assert.Throws<ArgumentNullException>(() =>
            new EfCoreServiceSetupCodeStore<SetupCodeDbContext>(null!));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.CreateAsync(Service, cancelled.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.RotateAsync(Service, cancelled.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.ValidateAsync(Service, "candidate", cancelled.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.StageConsumeAsync(Service, "candidate", cancelled.Token).AsTask());
    }

    [Fact]
    public async Task A_configured_lifetime_is_applied()
    {
        await using var harness = await Harness.CreateAsync();
        var store = new EfCoreServiceSetupCodeStore<SetupCodeDbContext>(
            harness.Context,
            harness.Time,
            SetupCodeLifetime.Create(TimeSpan.FromHours(24)));

        var result = await store.CreateAsync(Service, TestContext.Current.CancellationToken);

        Assert.Equal(Now.UtcDateTime.AddHours(24), result.ExpiresAtUtc);
        harness.Time.UtcNow = Now.AddHours(23);
        Assert.True((await store.ValidateAsync(
            Service,
            result.SetupCode!.Reveal(),
            TestContext.Current.CancellationToken)).IsValid);
    }

    [Fact]
    public async Task An_undefined_installation_status_stays_a_closed_rejection_for_every_operation()
    {
        // The base state mapper reports an undefined status as installation.entity_invalid, which is
        // not a Setup Code classification. Every operation must still answer with the declared
        // installation.state_invariant_violation instead of breaking the closed result contract.
        await using var harness = await Harness.CreateAsync();
        await harness.MutateAsync(row => row.Status = (InstallationStatus)99);
        var store = harness.Store();
        var candidate = new string('a', SetupCode.Length);

        Assert.Equal(
            WellKnownSetupCodeErrorCodes.StateInvariantViolation,
            (await store.CreateAsync(Service, TestContext.Current.CancellationToken)).ErrorCode);
        Assert.Equal(
            WellKnownSetupCodeErrorCodes.StateInvariantViolation,
            (await store.RotateAsync(Service, TestContext.Current.CancellationToken)).ErrorCode);
        Assert.Equal(
            WellKnownSetupCodeErrorCodes.StateInvariantViolation,
            (await store.ValidateAsync(Service, candidate, TestContext.Current.CancellationToken)).ErrorCode);
        Assert.Equal(
            WellKnownSetupCodeErrorCodes.StateInvariantViolation,
            (await store.StageConsumeAsync(Service, candidate, TestContext.Current.CancellationToken)).ErrorCode);

        var row = await harness.ReadAsync();
        Assert.Equal((InstallationStatus)99, row.Status);
        Assert.Equal(1, row.Version);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("rotate")]
    [InlineData("validate")]
    [InlineData("stage-consume")]
    public async Task A_read_failure_uses_the_safe_storage_error_channel(string operation)
    {
        await using var harness = await Harness.CreateAsync();
        await using var failing = harness.NewContext(new ThrowingCommandInterceptor(
            () => new InvalidOperationException("Host=db;Password=hunter2 provider read failure")));
        var store = new EfCoreServiceSetupCodeStore<SetupCodeDbContext>(failing, harness.Time);
        var candidate = new string('a', SetupCode.Length);

        var exception = await Assert.ThrowsAsync<ServiceInstallationStoreException>(() => operation switch
        {
            "create" => store.CreateAsync(Service, TestContext.Current.CancellationToken).AsTask(),
            "rotate" => store.RotateAsync(Service, TestContext.Current.CancellationToken).AsTask(),
            "validate" => store
                .ValidateAsync(Service, candidate, TestContext.Current.CancellationToken)
                .AsTask(),
            _ => store
                .StageConsumeAsync(Service, candidate, TestContext.Current.CancellationToken)
                .AsTask(),
        });

        Assert.Equal("installation.storage_error", exception.ErrorCode);
        Assert.DoesNotContain("provider read failure", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Host=db", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(candidate, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_at_the_read_boundary_is_not_reported_as_a_storage_failure()
    {
        await using var harness = await Harness.CreateAsync();
        await using var failing = harness.NewContext(
            new ThrowingCommandInterceptor(() => new OperationCanceledException()));
        var store = new EfCoreServiceSetupCodeStore<SetupCodeDbContext>(failing, harness.Time);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.ValidateAsync(
                Service,
                new string('a', SetupCode.Length),
                TestContext.Current.CancellationToken).AsTask());
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private Harness(SqliteConnection connection, SetupCodeDbContext context, MutableTimeProvider time)
        {
            this.connection = connection;
            Context = context;
            Time = time;
        }

        internal SetupCodeDbContext Context { get; }

        internal MutableTimeProvider Time { get; }

        internal static async Task<Harness> CreateAsync(bool seed = true)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var context = NewContext(connection);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            var harness = new Harness(connection, context, new MutableTimeProvider(Now));
            if (seed)
            {
                await harness.SeedAsync(Service.Value);
                context.ChangeTracker.Clear();
            }

            return harness;
        }

        internal SetupCodeDbContext NewContext(IInterceptor? interceptor = null) =>
            NewContext(connection, interceptor);

        internal EfCoreServiceSetupCodeStore<SetupCodeDbContext> Store() => new(Context, Time);

        internal async Task SeedAsync(string serviceId)
        {
            Context.ServiceInstallations.Add(new ServiceInstallationEntity
            {
                ServiceId = serviceId,
                Status = InstallationStatus.PendingSetup,
                CreatedAtUtc = CreatedAtUtc,
                CompletedAtUtc = null,
                Version = 1,
                SetupCodeGeneration = 0,
            });
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        internal async Task<ServiceInstallationEntity> ReadAsync(string? serviceId = null)
        {
            await using var context = NewContext();
            return await context.ServiceInstallations
                .AsNoTracking()
                .SingleAsync(
                    item => item.ServiceId == (serviceId ?? Service.Value),
                    TestContext.Current.CancellationToken);
        }

        internal async Task MutateAsync(Action<ServiceInstallationEntity> mutate)
        {
            await using var context = NewContext();
            var row = await context.ServiceInstallations.SingleAsync(
                item => item.ServiceId == Service.Value,
                TestContext.Current.CancellationToken);
            mutate(row);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            Context.ChangeTracker.Clear();
        }

        internal Task SetMaterialAsync(
            int generation,
            string? digest,
            DateTime? issuedAtUtc,
            DateTime? expiresAtUtc) =>
            MutateAsync(row =>
            {
                row.SetupCodeGeneration = generation;
                row.SetupCodeDigest = digest;
                row.SetupCodeIssuedAtUtc = issuedAtUtc;
                row.SetupCodeExpiresAtUtc = expiresAtUtc;
            });

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }

        private static SetupCodeDbContext NewContext(
            SqliteConnection connection,
            IInterceptor? interceptor = null)
        {
            var builder = new DbContextOptionsBuilder<SetupCodeDbContext>().UseSqlite(connection);
            if (interceptor is not null)
            {
                builder.AddInterceptors(interceptor);
            }

            return new SetupCodeDbContext(builder.Options);
        }
    }

    private sealed class SetupCodeDbContext(DbContextOptions<SetupCodeDbContext> options)
        : DbContext(options), IServiceMantleDbContext
    {
        internal Func<CancellationToken, Task>? BeforeSaveChangesAsync { get; set; }

        public DbSet<ServiceInstallationEntity> ServiceInstallations => Set<ServiceInstallationEntity>();

        public override async Task<int> SaveChangesAsync(
            CancellationToken cancellationToken = default)
        {
            if (BeforeSaveChangesAsync is { } callback)
            {
                BeforeSaveChangesAsync = null;
                await callback(cancellationToken);
            }

            return await base.SaveChangesAsync(cancellationToken);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.AddServiceMantleInstallation();
    }

    private sealed class ThrowingCommandInterceptor(Func<Exception> failure) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result) => throw failure();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default) => throw failure();
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
