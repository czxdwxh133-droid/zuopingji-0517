using Microsoft.EntityFrameworkCore;

namespace NewsBriefingAssistant.DatabaseContracts;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<BriefingTask> BriefingTasks => Set<BriefingTask>();
    public DbSet<BriefingArticleResult> BriefingArticleResults => Set<BriefingArticleResult>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BriefingArticleResult>()
            .HasOne(r => r.BriefingTask)
            .WithMany(t => t.ArticleResults)
            .HasForeignKey(r => r.BriefingTaskId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
