using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Tickwire.Persistence;

public sealed class ClientEntity
{
    public required string ClientId { get; set; }
    public required string DisplayName { get; set; }
    public bool IsGuest { get; set; }
    public bool KillSwitch { get; set; }

    /// <summary>Opaque token the owner uses to manage this client through the public API (hashed).</summary>
    public string? OwnerTokenHash { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }

    public RiskLimitsEntity? Limits { get; set; }
    public List<FixSessionEntity> Sessions { get; set; } = [];
}

public sealed class RiskLimitsEntity
{
    public required string ClientId { get; set; }
    public decimal MaxOrderQty { get; set; }
    public decimal MaxNotional { get; set; }
    public decimal PriceBandPct { get; set; }
    public decimal PriceBandMinAbs { get; set; }
    public int MaxOpenOrders { get; set; }

    /// <summary>Comma-separated underlyings; null or empty means all.</summary>
    public string? AllowedUnderlyings { get; set; }

    /// <summary>Comma-separated: Limit,Market.</summary>
    public required string AllowedOrderTypes { get; set; }

    /// <summary>Comma-separated: Day,ImmediateOrCancel,FillOrKill.</summary>
    public required string AllowedTimeInForce { get; set; }

    public int MaxMessagesPerSecond { get; set; }
    public bool CancelOnDisconnect { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>One FIX session a client may log on with. CompIDs are from the client's point of view.</summary>
public sealed class FixSessionEntity
{
    public int Id { get; set; }
    public required string ClientId { get; set; }
    public required string BeginString { get; set; }

    /// <summary>The client's SenderCompID(49) on inbound messages.</summary>
    public required string ClientCompId { get; set; }

    /// <summary>Our CompID: the client's TargetCompID(56).</summary>
    public required string VenueCompId { get; set; }

    public int HeartBtInt { get; set; }
    public bool ResetOnLogon { get; set; }

    /// <summary>tcp, websocket, or memory (the browser trader's in-process session).</summary>
    public required string Transport { get; set; }

    public bool EnableChaos { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class SessionStateEntity
{
    public required string SessionKey { get; set; }
    public int NextSenderSeqNum { get; set; }
    public int NextTargetSeqNum { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Every FIX message in or out. Outbound rows double as the resend store.</summary>
public sealed class SessionMessageEntity
{
    public long Id { get; set; }
    public required string SessionKey { get; set; }
    public char Direction { get; set; }
    public int SeqNum { get; set; }
    public required string MsgType { get; set; }
    public required byte[] Raw { get; set; }
    public required string Disposition { get; set; }
    public bool IsStore { get; set; }
    public DateTime Timestamp { get; set; }
}

public sealed class OrderEntity
{
    public long Id { get; set; }
    public required string OrderId { get; set; }
    public required string ClientId { get; set; }
    public required string ClOrdID { get; set; }
    public string? OrigClOrdID { get; set; }
    public required string Symbol { get; set; }
    public char Side { get; set; }
    public char OrdType { get; set; }
    public char TimeInForce { get; set; }
    public decimal? Price { get; set; }
    public decimal OrderQty { get; set; }
    public decimal CumQty { get; set; }
    public decimal LeavesQty { get; set; }
    public decimal AvgPx { get; set; }
    public char Status { get; set; }
    public string? Text { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class ExecutionEntity
{
    public required string ExecId { get; set; }
    public long OrderKey { get; set; }
    public required string ClientId { get; set; }
    public required string ClOrdID { get; set; }
    public char ExecType { get; set; }
    public char OrdStatus { get; set; }
    public decimal LastQty { get; set; }
    public decimal LastPx { get; set; }
    public decimal CumQty { get; set; }
    public decimal LeavesQty { get; set; }
    public string? Text { get; set; }
    public DateTime TransactTime { get; set; }
}

public sealed class AuditEntity
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public string? ClientId { get; set; }
    public string? Details { get; set; }
}

public sealed class TickwireDbContext(DbContextOptions<TickwireDbContext> options) : DbContext(options)
{
    public DbSet<ClientEntity> Clients => Set<ClientEntity>();
    public DbSet<RiskLimitsEntity> RiskLimits => Set<RiskLimitsEntity>();
    public DbSet<FixSessionEntity> FixSessions => Set<FixSessionEntity>();
    public DbSet<SessionStateEntity> SessionStates => Set<SessionStateEntity>();
    public DbSet<SessionMessageEntity> SessionMessages => Set<SessionMessageEntity>();
    public DbSet<OrderEntity> Orders => Set<OrderEntity>();
    public DbSet<ExecutionEntity> Executions => Set<ExecutionEntity>();
    public DbSet<AuditEntity> AuditLog => Set<AuditEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ClientEntity>(e =>
        {
            e.ToTable("clients");
            e.HasKey(x => x.ClientId);
            e.Property(x => x.ClientId).HasMaxLength(64);
            e.Property(x => x.DisplayName).HasMaxLength(128);
            e.Property(x => x.OwnerTokenHash).HasMaxLength(64);
            e.HasIndex(x => x.ExpiresAt);
            e.HasOne(x => x.Limits).WithOne().HasForeignKey<RiskLimitsEntity>(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Sessions).WithOne().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RiskLimitsEntity>(e =>
        {
            e.ToTable("risk_limits");
            e.HasKey(x => x.ClientId);
            e.Property(x => x.ClientId).HasMaxLength(64);
            e.Property(x => x.MaxOrderQty).HasPrecision(18, 4);
            e.Property(x => x.MaxNotional).HasPrecision(18, 2);
            e.Property(x => x.PriceBandPct).HasPrecision(9, 4);
            e.Property(x => x.PriceBandMinAbs).HasPrecision(18, 4);
            e.Property(x => x.AllowedUnderlyings).HasMaxLength(256);
            e.Property(x => x.AllowedOrderTypes).HasMaxLength(64);
            e.Property(x => x.AllowedTimeInForce).HasMaxLength(64);
        });

        b.Entity<FixSessionEntity>(e =>
        {
            e.ToTable("fix_sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.ClientId).HasMaxLength(64);
            e.Property(x => x.BeginString).HasMaxLength(16);
            e.Property(x => x.ClientCompId).HasMaxLength(64);
            e.Property(x => x.VenueCompId).HasMaxLength(64);
            e.Property(x => x.Transport).HasMaxLength(16);
            e.HasIndex(x => new { x.BeginString, x.ClientCompId, x.VenueCompId }).IsUnique();
        });

        b.Entity<SessionStateEntity>(e =>
        {
            e.ToTable("session_state");
            e.HasKey(x => x.SessionKey);
            e.Property(x => x.SessionKey).HasMaxLength(160);
        });

        b.Entity<SessionMessageEntity>(e =>
        {
            e.ToTable("session_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.SessionKey).HasMaxLength(160);
            e.Property(x => x.MsgType).HasMaxLength(8);
            e.Property(x => x.Disposition).HasMaxLength(24);
            e.Property(x => x.Raw).HasColumnType("varbinary(8192)");
            e.HasIndex(x => new { x.SessionKey, x.IsStore, x.SeqNum });
            e.HasIndex(x => new { x.SessionKey, x.Timestamp });
        });

        b.Entity<OrderEntity>(e =>
        {
            e.ToTable("orders");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.OrderId).HasMaxLength(24);
            e.Property(x => x.ClientId).HasMaxLength(64);
            e.Property(x => x.ClOrdID).HasMaxLength(64);
            e.Property(x => x.OrigClOrdID).HasMaxLength(64);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Text).HasMaxLength(512);
            e.Property(x => x.Price).HasPrecision(18, 4);
            e.Property(x => x.OrderQty).HasPrecision(18, 4);
            e.Property(x => x.CumQty).HasPrecision(18, 4);
            e.Property(x => x.LeavesQty).HasPrecision(18, 4);
            e.Property(x => x.AvgPx).HasPrecision(18, 6);
            e.HasIndex(x => new { x.ClientId, x.CreatedAt });
        });

        b.Entity<ExecutionEntity>(e =>
        {
            e.ToTable("executions");
            e.HasKey(x => x.ExecId);
            e.Property(x => x.ExecId).HasMaxLength(32);
            e.Property(x => x.ClientId).HasMaxLength(64);
            e.Property(x => x.ClOrdID).HasMaxLength(64);
            e.Property(x => x.Text).HasMaxLength(512);
            e.Property(x => x.LastQty).HasPrecision(18, 4);
            e.Property(x => x.LastPx).HasPrecision(18, 4);
            e.Property(x => x.CumQty).HasPrecision(18, 4);
            e.Property(x => x.LeavesQty).HasPrecision(18, 4);
            e.HasIndex(x => x.OrderKey);
        });

        b.Entity<AuditEntity>(e =>
        {
            e.ToTable("audit_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.Actor).HasMaxLength(64);
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.ClientId).HasMaxLength(64);
            e.Property(x => x.Details).HasMaxLength(2048);
            e.HasIndex(x => x.Timestamp);
        });
    }
}

/// <summary>Lets `dotnet ef migrations add` build the model without a live database.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<TickwireDbContext>
{
    public TickwireDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<TickwireDbContext>()
            .UseMySql("Server=localhost;Database=tickwire", MySqlServer.Version)
            .Options);
}

public static class MySqlServer
{
    /// <summary>Fixed server version so the model never needs a live connection to be built.</summary>
    public static readonly ServerVersion Version = new MySqlServerVersion(new Version(8, 0, 36));
}
