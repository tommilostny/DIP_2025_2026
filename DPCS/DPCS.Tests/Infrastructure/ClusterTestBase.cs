using DPCS.DAL;
using Microsoft.EntityFrameworkCore;
using Proto.Cluster.Testing;
using Testcontainers.PostgreSql;

namespace DPCS.Tests.Infrastructure;

// Using a collection forces xUnit to run tests deriving from this base sequentially.
// This is critical for performance evaluation to prevent thread contention from skewing benchmarks.
[Collection("Performance Evaluation")]
public abstract class ClusterTestBase : IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer;
    
    // Protected DbContext so test scenarios can easily assert on the database state
    protected DpcsDbContext DbContext { get; private set; } = null!;
    protected string DbConnectionString => _dbContainer.GetConnectionString();

    // The shared in-memory Consul mock that all nodes in this test will use to discover each other
    protected InMemAgent ConsulMock { get; private set; } = null!;

    protected ClusterTestBase()
    {
        // Define the ephemeral PostgreSQL container
        _dbContainer = new PostgreSqlBuilder("postgres:15-alpine")
            .WithDatabase("dpcs_test")
            .WithUsername("postgres")
            .WithPassword("testpassword123")
            .WithCleanUp(true) // Automatically remove container from Docker on test exit
            .Build();
    }

    public virtual async Task InitializeAsync()
    {
        // 1. Spin up the clean database container
        await _dbContainer.StartAsync();

        // 2. Configure EF Core to point to this fresh container
        var options = new DbContextOptionsBuilder<DpcsDbContext>()
            .UseNpgsql(DbConnectionString)
            .Options;

        DbContext = new DpcsDbContext(options);
        
        // 3. Apply schema to the empty database
        await DbContext.Database.EnsureCreatedAsync();
        
        // 4. Bootstrap the in-memory cluster network
        await InitializeClusterAsync();
    }

    public virtual async Task DisposeAsync()
    {
        await DisposeClusterAsync();
        
        if (DbContext is not null)
        {
            await DbContext.DisposeAsync();
        }
        
        // Tears down the Docker container
        await _dbContainer.DisposeAsync();
    }

    protected virtual Task InitializeClusterAsync()
    {
        ConsulMock = new InMemAgent();
        
        // Specific node initialization (Manager, Coordinator) will be performed 
        // in the test setup methods so that each test can inject custom dependencies 
        // (e.g., specific DummyHashcatWrapper configurations).
        return Task.CompletedTask;
    }

    protected virtual Task DisposeClusterAsync()
    {
        return Task.CompletedTask;
    }
}