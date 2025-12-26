#!/bin/bash
# Fix CA1707 errors in test project by disabling the rule
# CA1707 forbids underscores in identifiers, but Method_Scenario_Expected is 
# a common test naming convention that should be allowed in test projects.

set -euo pipefail

cd ~/src/dotnet/network-monitor/src

echo "Fixing CA1707 errors in test project..."

# Add NoWarn for CA1707 to the test project
# This is the standard approach - test naming conventions often use underscores
cat > NetworkMonitor.Tests/NetworkMonitor.Tests.csproj << 'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <!--
    Unit tests using xUnit 3.
    
    Testing approach:
    - Use manual fakes/stubs instead of mocking frameworks (Moq is banned)
    - Focus on behavior, not implementation details
    - Each test class tests one component in isolation
    - Integration tests can test multiple components together
  -->
  <PropertyGroup>
    <!-- 
      CA1707: Identifiers should not contain underscores
      Disabled because test methods commonly use Method_Scenario_Expected naming.
    -->
    <NoWarn>$(NoWarn);CA1707</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\NetworkMonitor.Core\NetworkMonitor.Core.csproj" />
  </ItemGroup>
</Project>
EOF

echo "Done! CA1707 is now suppressed for the test project."
echo "Run 'dotnet build' to verify the fix."
