using System.Reflection;
using KidShell.Core.Security.Readiness;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// The dry-run guarantee.
///
/// These are structural tests rather than behavioural ones: they assert that
/// the code has no way to change Windows, so the guarantee survives future
/// edits instead of resting on the current author's intent.
/// </summary>
public class SecurityExecutionModeTests
{
    [Fact]
    public void The_only_context_that_can_be_created_is_AuditOnly()
    {
        var context = SecurityExecutionContext.AuditOnly();

        Assert.Equal(SecurityExecutionMode.AuditOnly, context.Mode);
        Assert.True(context.IsAuditOnly);
    }

    [Fact]
    public void There_is_no_public_factory_that_produces_an_Apply_context()
    {
        // Any public/static member returning a context must be the AuditOnly
        // factory. A future milestone adding an Apply factory will fail here,
        // which is the point: it should be a deliberate, reviewed change.
        var factories = typeof(SecurityExecutionContext)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(SecurityExecutionContext))
            .ToArray();

        var factory = Assert.Single(factories);
        Assert.Equal(nameof(SecurityExecutionContext.AuditOnly), factory.Name);
        Assert.Empty(factory.GetParameters());
    }

    [Fact]
    public void The_context_cannot_be_constructed_directly()
    {
        var constructors = typeof(SecurityExecutionContext)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        Assert.Empty(constructors);
    }

    [Fact]
    public void The_mode_is_read_only_once_a_context_exists()
    {
        var property = typeof(SecurityExecutionContext).GetProperty(nameof(SecurityExecutionContext.Mode))!;

        Assert.True(property.CanRead);
        Assert.False(property.CanWrite);
    }

    [Fact]
    public void Nothing_in_the_solution_implements_ISecurityMutator()
    {
        // ISecurityMutator is the declared boundary at which KidShell would
        // change Windows. While no type implements it, the application has no
        // mutation path at all - not a disabled one, not a guarded one, none.
        var assemblies = new[]
        {
            typeof(SecurityExecutionContext).Assembly,   // KidShell.Core
            typeof(SecurityExecutionModeTests).Assembly  // the test assembly
        };

        foreach (var assembly in assemblies)
        {
            var implementations = assembly
                .GetTypes()
                .Where(t => typeof(ISecurityMutator).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false })
                .ToArray();

            Assert.Empty(implementations);
        }
    }

    [Fact]
    public void The_readiness_service_takes_no_dependency_that_could_mutate_windows()
    {
        var constructor = Assert.Single(typeof(SecurityReadinessService).GetConstructors());

        var parameterTypes = constructor.GetParameters().Select(p => p.ParameterType).ToArray();

        Assert.DoesNotContain(typeof(ISecurityMutator), parameterTypes);
        Assert.DoesNotContain(typeof(SecurityExecutionContext), parameterTypes);
    }

    [Fact]
    public void Account_discovery_exposes_no_way_to_change_an_account()
    {
        // The interface is the contract a future implementation must satisfy.
        // Keeping it read-only means account management needs a new, visibly
        // named interface rather than an extra method here.
        var methods = typeof(IWindowsAccountDiscovery).GetMethods();

        var method = Assert.Single(methods);
        Assert.Equal(nameof(IWindowsAccountDiscovery.ListLocalAccountsAsync), method.Name);

        foreach (var forbidden in new[] { "Create", "Add", "Delete", "Remove", "Set", "Update", "Disable", "Enable", "Password" })
        {
            Assert.DoesNotContain(
                methods,
                m => m.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void The_system_facts_provider_only_reads()
    {
        var methods = typeof(ISystemFactsProvider).GetMethods();

        var method = Assert.Single(methods);
        Assert.Equal(nameof(ISystemFactsProvider.ReadAsync), method.Name);
    }

    [Fact]
    public void A_report_can_never_claim_windows_lockdown_is_enabled()
    {
        var report = new SecurityReadinessReport
        {
            OverallState = ReadinessState.Ready,
            RecommendedMode = SecurityMode.Secure,
            CurrentMode = SecurityMode.Development,
            Capabilities = WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Enterprise()),
            ExecutionMode = SecurityExecutionMode.AuditOnly,
            ScannedAtUtc = DateTimeOffset.UtcNow
        };

        // Computed, with no setter: there is no code path in 0.1.x that could
        // make this true, because there is no code that applies anything.
        Assert.False(report.WindowsLockdownEnabled);
        Assert.Null(typeof(SecurityReadinessReport)
            .GetProperty(nameof(SecurityReadinessReport.WindowsLockdownEnabled))!
            .SetMethod);
    }
}
