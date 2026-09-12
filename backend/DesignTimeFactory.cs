using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Story;

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<StoryDb>
{
    public StoryDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<StoryDb>()
        .UseNpgsql("Host=localhost;Database=storyapp;Username=storyapp;Password=design-time-only") .Options);
}
