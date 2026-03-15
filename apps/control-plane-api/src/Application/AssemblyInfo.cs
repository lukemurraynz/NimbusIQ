using System.Runtime.CompilerServices;

// Allow the unit-test assembly to access internal members of this project
// (e.g. ScoringService.CalculateCompleteness, CalculateSecurityScore, DetectProductionEnvironment).
[assembly: InternalsVisibleTo("Atlas.ControlPlane.Tests.Unit")]
