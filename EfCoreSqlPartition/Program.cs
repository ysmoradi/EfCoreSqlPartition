using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data.SqlTypes;
using System.Linq;
using System.Threading.Tasks;

namespace EfCoreSqlPartition
{
    class Program
    {
        static async Task Main(string[] args)
        {
            ServiceCollection services = new ServiceCollection();

            services.AddDbContext<AppDbContext>();

            services.AddLogging(loggingBuilder =>
            {
                loggingBuilder.AddConsole();
            });

            IServiceProvider serviceProvider = services.BuildServiceProvider();

            for (int i = 0; i < 149_990; i++)
            {
                AppDbContext dbContext = serviceProvider.GetRequiredService<AppDbContext>();

                var business = (await dbContext.Businesses.AddAsync(new Business { Name = "Test1" })).Entity;

                await dbContext.SaveChangesAsync();

                for (int j = 0; j < 6; j++)
                {
                    var customer = (await dbContext.Customers.AddAsync(new Customer { BusinessId = business.Id, Name = "Customer1" })).Entity;

                    await dbContext.SaveChangesAsync();

                    var order = await dbContext.Orders.AddAsync(new Order { CustomerId = customer.Id, Date = DateTimeOffset.UtcNow, BusinessId = business.Id });

                    await dbContext.SaveChangesAsync();
                }

                /*await using AppDbContext dbContext2 = serviceProvider.GetRequiredService<AppDbContext>();

                var customers = await dbContext.Customers
                    .Select(c => new
                    {
                        c.Id,
                        c.Name,
                        c.Orders.Count
                    })
                    .ToArrayAsync();*/
            }
        }
    }

    public class CustomSqlServerMigrationsSqlGenerator : SqlServerMigrationsSqlGenerator
    {
        private readonly MigrationsSqlGeneratorDependencies _dependencies;

        public CustomSqlServerMigrationsSqlGenerator(MigrationsSqlGeneratorDependencies dependencies, IMigrationsAnnotationProvider migrationsAnnotations)
            : base(dependencies, migrationsAnnotations)
        {
            _dependencies = dependencies;
        }

        protected override void Generate(CreateTableOperation operation, IModel model, MigrationCommandListBuilder builder, bool terminate = true)
        {
            if (operation.Columns.Any(c => c.Name == "BusinessId"))
            {
                MigrationCommandListBuilder tempBuilder = new MigrationCommandListBuilder(_dependencies);

                base.Generate(operation, model, tempBuilder, terminate: false);

                var createTableCommand = tempBuilder.GetCommandList().Single().CommandText;

                builder.AppendLine($"{createTableCommand.Substring(0, createTableCommand.Length - 3)} ON BusinessPartitionScheme(BusinessId);");
            }
            else
            {
                base.Generate(operation, model, builder, terminate: true);
            }
        }

        protected override void Generate(CreateIndexOperation operation, IModel model, MigrationCommandListBuilder builder, bool terminate)
        {
            base.Generate(operation, model, builder, false);

            if (operation.Columns.Any(c => c == "BusinessId"))
            {
                builder.Append($" ON BusinessPartitionScheme(BusinessId);");
            }

            if (terminate)
            {
                builder.AppendLine(";");
                EndStatement(builder);
            }
        }
    }

    public class AppDbContext : DbContext
    {
        public AppDbContext()
        {

        }

        public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options)
        {
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.EnableSensitiveDataLogging();

            optionsBuilder.ReplaceService<IMigrationsSqlGenerator, CustomSqlServerMigrationsSqlGenerator>();

            optionsBuilder.UseSqlServer(@"Data Source=.;Initial Catalog=AppDb;Integrated Security=True");

            base.OnConfiguring(optionsBuilder);
        }

        public DbSet<Business> Businesses { get; set; }

        public DbSet<Customer> Customers { get; set; }

        public DbSet<Order> Orders { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Business>()
                .Property(business => business.Id)
                .HasDefaultValueSql("dbo.BusinessIdProvider()");

            modelBuilder.Entity<Customer>()
                .HasKey(customer => new { customer.Id, customer.BusinessId });

            modelBuilder.Entity<Customer>()
                .Property(customer => customer.Id)
                .HasDefaultValueSql("NewSequentialId()");

            modelBuilder.Entity<Customer>()
                .HasMany(customer => customer.Orders)
                .WithOne(order => order.Customer)
                .HasForeignKey(o => new { o.CustomerId, o.BusinessId });

            modelBuilder.Entity<Order>()
                .HasKey(order => new { order.Id, order.BusinessId });

            modelBuilder.Entity<Order>()
                .Property(order => order.Id)
                .HasDefaultValueSql("NewSequentialId()");

            ConfigureCascades(modelBuilder);

            base.OnModelCreating(modelBuilder);
        }

        void ConfigureCascades(ModelBuilder builder)
        {
            IEnumerable<IMutableForeignKey> cascadeFKs = builder.Model.GetEntityTypes()
                .SelectMany(t => t.GetForeignKeys())
                .Where(fk => !fk.IsOwnership && fk.DeleteBehavior == DeleteBehavior.Cascade);

            foreach (IMutableForeignKey fk in cascadeFKs)
                fk.DeleteBehavior = DeleteBehavior.Restrict;
        }
    }

    public interface IBusinessAware
    {
        public Guid BusinessId { get; set; }

        public Business Business { get; set; }
    }

    [Table("Businesses")]
    public class Business
    {
        [Key]
        public Guid Id { get; set; }

        [StringLength(50)]
        public string Name { get; set; }
    }

    [Table("Customers")]
    public class Customer : IBusinessAware
    {
        [Key]
        public Guid Id { get; set; }

        public string Name { get; set; }

        [ForeignKey(nameof(BusinessId))]
        public Business Business { get; set; }
        public Guid BusinessId { get; set; }

        public List<Order> Orders { get; set; }
    }

    [Table("Orders")]
    public class Order : IBusinessAware
    {
        public Guid Id { get; set; }

        public DateTimeOffset Date { get; set; }

        [ForeignKey(nameof(BusinessId))]
        public Business Business { get; set; }
        public Guid BusinessId { get; set; }

        public Customer Customer { get; set; }
        public Guid CustomerId { get; set; }
    }

    public static class MigrationBuilderExtensions
    {
        public static void PrepareBusinessBasedPartitioning(this MigrationBuilder migrationBuilder)
        {
            SqlGuid[] businessIds = Enumerable.Range(1, 149_990)
                .Select(_ => (SqlGuid)Guid.NewGuid())
                .OrderBy(_ => _)
                .ToArray();

            SqlGuid[] partitionIds = businessIds
                .Where((_, index) => index % 10 == 0) // sql server max supports 15_000 partitions => 149_990 / 10 => 14_999
                .ToArray();

            migrationBuilder.Sql($"create partition function BusinessPartitioner (uniqueidentifier) as range left for values ({string.Join(",", partitionIds.Select(business => $"'{business}'"))})");

            migrationBuilder.Sql($"create partition scheme BusinessPartitionScheme as partition BusinessPartitioner all to ([PRIMARY])");

            migrationBuilder.Sql($"create table BusinessIds([Id] [uniqueidentifier] NOT NULL, CONSTRAINT [PK_BusinessIds] PRIMARY KEY CLUSTERED ([Id] ASC))");

            migrationBuilder.Sql(@"
create function BusinessIdProvider()
returns uniqueidentifier
as
begin
	return(select min(BusinessId.Id) from BusinessIds as BusinessId where BusinessId.Id > (select isnull(max(Business.Id), '00000000-0000-0000-0000-000000000000') from Businesses as Business))
end");

            foreach (Guid businessId in businessIds)
            {
                migrationBuilder.Sql($"insert into BusinessIds (Id) values('{businessId}')");
            }
        }
    }
}