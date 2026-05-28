# MSPChallenge Client Browser - Test Suite

Automated test suite for the MSPChallenge Blazor Server application.

## Test Categories

### Unit Tests (`UnitTests/`)
- **PlanApprovalTests**: Tests for `CalculateApproval()` logic
- **PlanStateTransitionTests**: Tests for plan state machine
- **BatchRequestBuildingTests**: Tests for batch API request construction

### Integration Tests (`IntegrationTests/`)
- **WebSocketMessageParsingTests**: Tests for WebSocket message parsing

## Running Tests

### All Tests
dotnet test

### With Coverage
dotnet test --collect:"XPlat Code Coverage"

### Specific Category
dotnet test --filter "FullyQualifiedName~UnitTests"
dotnet test --filter "FullyQualifiedName~IntegrationTests"

### Watch Mode (auto-rerun on changes)
dotnet watch test

## Test Principles
1. Fast: Unit tests run in milliseconds
2. Isolated: No database or network dependencies in unit tests
3. Repeatable: Same input = same output every time
4. Readable: Test names describe what they test
5. Maintainable: Helper methods reduce duplication

## Adding New Tests
When adding a new feature:

1. Write unit tests for business logic first
2. Add integration tests for API interactions
3. Consider component tests for complex UI logic
4. Update this README if adding new test categories

## Coverage Goals
- Unit Tests: 80%+ coverage of business logic
- Integration Tests: All critical API paths
- E2E Tests: Core user workflows