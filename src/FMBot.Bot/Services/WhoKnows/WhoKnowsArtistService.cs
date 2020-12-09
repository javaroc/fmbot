using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dasync.Collections;
using Discord.Commands;
using FMBot.Bot.Models;
using FMBot.Persistence.Domain.Models;
using FMBot.Persistence.EntityFrameWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FMBot.Bot.Services.WhoKnows
{
    public class WhoKnowsArtistService
    {
        private readonly IMemoryCache _cache;
        private readonly IDbContextFactory<FMBotDbContext> _contextFactory;

        public WhoKnowsArtistService(IMemoryCache cache, IDbContextFactory<FMBotDbContext> contextFactory)
        {
            this._cache = cache;
            this._contextFactory = contextFactory;
        }

        public async Task<IList<WhoKnowsObjectWithUser>> GetIndexedUsersForArtist(ICommandContext context,
            ICollection<GuildUser> guildUsers, string artistName)
        {
            var userIds = guildUsers.Select(s => s.UserId);

            await using var db = this._contextFactory.CreateDbContext();
            var userArtists = await db.UserArtists
                .Include(i => i.User)
                .Where(w =>
                    userIds.Contains(w.UserId) &&
                    EF.Functions.ILike(w.Name, artistName))
                .OrderByDescending(o => o.Playcount)
                .Take(14)
                .ToListAsync();

            var whoKnowsArtistList = new List<WhoKnowsObjectWithUser>();

            foreach (var userArtist in userArtists)
            {
                var discordUser = await context.Guild.GetUserAsync(userArtist.User.DiscordUserId);
                var guildUser = guildUsers.FirstOrDefault(f => f.UserId == userArtist.UserId);
                var userName = discordUser != null ?
                    discordUser.Nickname ?? discordUser.Username :
                    guildUser?.UserName ?? userArtist.User.UserNameLastFM;

                whoKnowsArtistList.Add(new WhoKnowsObjectWithUser
                {
                    Name = userArtist.Name,
                    DiscordName = userName,
                    Playcount = userArtist.Playcount,
                    LastFMUsername = userArtist.User.UserNameLastFM,
                    UserId = userArtist.UserId
                });
            }

            return whoKnowsArtistList;
        }

        public async Task<IReadOnlyList<ListArtist>> GetTopArtistsForGuild(IReadOnlyList<User> guildUsers,
            OrderType orderType)
        {
            var userIds = guildUsers.Select(s => s.UserId);

            await using var db = this._contextFactory.CreateDbContext();
            var query = db.UserArtists
                .AsQueryable()
                .Where(w => userIds.Contains(w.UserId))
                .GroupBy(o => o.Name);

            query = orderType == OrderType.Playcount ?
                query.OrderByDescending(o => o.Sum(s => s.Playcount)).ThenByDescending(o => o.Count()) :
                query.OrderByDescending(o => o.Count()).ThenByDescending(o => o.Sum(s => s.Playcount));

            return await query
                .Take(14)
                .Select(s => new ListArtist
                {
                    ArtistName = s.Key,
                    Playcount = s.Sum(su => su.Playcount),
                    ListenerCount = s.Count()
                })
                .ToListAsync();
        }

        public async Task<int> GetArtistListenerCountForServer(ICollection<GuildUser> guildUsers, string artistName)
        {
            var userIds = guildUsers.Select(s => s.UserId);

            await using var db = this._contextFactory.CreateDbContext();
            return await db.UserArtists
                .AsQueryable()
                .Where(w => userIds.Contains(w.UserId) &&
                            EF.Functions.ILike(w.Name, artistName))
                .CountAsync();
        }

        public async Task<int> GetArtistPlayCountForServer(ICollection<GuildUser> guildUsers, string artistName)
        {
            var userIds = guildUsers.Select(s => s.UserId);

            await using var db = this._contextFactory.CreateDbContext();
            var query = db.UserArtists
                .AsQueryable()
                .Where(w => EF.Functions.ILike(w.Name, artistName)
                            && userIds.Contains(w.UserId));

            // This is bad practice, but it helps with speed. An exception gets thrown if the artist does not exist in the database.
            // Checking if the records exist first would be an extra database call
            try
            {
                return await query.SumAsync(s => s.Playcount);
            }
            catch
            {
                return 0;
            }
        }

        public async Task<int?> GetArtistPlayCountForUser(string artistName, int userId)
        {
            await using var db = this._contextFactory.CreateDbContext();
            var userArtist = await db.UserArtists
                .AsQueryable()
                .FirstOrDefaultAsync(w =>
                    w.UserId == userId &&
                    EF.Functions.ILike(w.Name, artistName));

            return userArtist?.Playcount;
        }

        public async Task<double> GetArtistAverageListenerPlaycountForServer(ICollection<GuildUser> guildUsers, string artistName)
        {
            var userIds = guildUsers.Select(s => s.UserId);

            await using var db = this._contextFactory.CreateDbContext();
            var query = db.UserArtists
                .AsQueryable()
                .Where(w =>
                    userIds.Contains(w.UserId) &&
                    EF.Functions.ILike(w.Name, artistName));

            try
            {
                return await query.AverageAsync(s => s.Playcount);
            }
            catch
            {
                return 0;
            }
        }

        public async Task<int> GetWeekArtistPlaycountForGuildAsync(ICollection<GuildUser> guildUsers, string artistName)
        {
            var now = DateTime.UtcNow;
            var minDate = DateTime.UtcNow.AddDays(-7);

            var userIds = guildUsers.Select(s => s.UserId);

            await using var db = this._contextFactory.CreateDbContext();
            return await db.UserPlays
                .AsQueryable()
                .CountAsync(a =>
                    userIds.Contains(a.UserId) &&
                    a.TimePlayed.Date <= now.Date &&
                    a.TimePlayed.Date > minDate.Date &&
                    EF.Functions.ILike(a.ArtistName, artistName.ToLower()));
        }

        // TODO: figure out how to do this
        public async Task<int> GetWeekArtistListenerCountForGuildAsync(IEnumerable<User> guildUsers, string artistName)
        {
            var now = DateTime.UtcNow;
            var minDate = DateTime.UtcNow.AddDays(-7);

            var userIds = guildUsers.Select(s => s.UserId);

            try
            {
                await using var db = this._contextFactory.CreateDbContext();
                return await db.UserPlays
                    .AsQueryable()
                    .Where(w =>
                        userIds.Contains(w.UserId) &&
                        w.TimePlayed.Date <= now.Date &&
                        w.TimePlayed.Date > minDate.Date &&
                        EF.Functions.ILike(w.ArtistName, artistName))
                    .GroupBy(x => new { x.UserId, x.ArtistName, x.UserPlayId })
                    .CountAsync();
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        public async Task<IReadOnlyList<AffinityArtistResultWithUser>> GetNeighbors(IEnumerable<User> guildUsers, int userId)
        {
            var userIds = guildUsers
                .Where(w => w.UserId != userId)
                .Select(s => s.UserId);

            var topArtistsForEveryoneInServer = new List<AffinityArtist>();

            await userIds.ParallelForEachAsync(async user =>
            {
                var key = $"top-artists-{user}";

                if (this._cache.TryGetValue(key, out List<AffinityArtist> topArtistsForUser))
                {
                    topArtistsForEveryoneInServer.AddRange(topArtistsForUser);
                }
                else
                {
                    await using var db = this._contextFactory.CreateDbContext();

                    var topArtist = await db.UserArtists
                        .AsQueryable()
                        .OrderByDescending(o => o.Playcount)
                        .FirstOrDefaultAsync(w => w.UserId == user);

                    var avgPlaycount = await db.UserArtists
                        .AsQueryable()
                        .Where(w => w.UserId == userId && w.Playcount > 29)
                        .AverageAsync(a => a.Playcount);

                    if (topArtist != null)
                    {
                        topArtistsForUser = await db.UserArtists
                            .AsQueryable()
                            .Where(
                                w => w.Playcount > 29 &&
                                     w.UserId == user &&
                                     w.Name != null)
                            .Select(s => new AffinityArtist
                            {
                                ArtistName = s.Name.ToLower(),
                                Playcount = s.Playcount,
                                UserId = s.UserId,
                                Weight = ((decimal)s.Playcount / (decimal)topArtist.Playcount) * (s.Playcount > (avgPlaycount * 2) ? 3 : 1)
                            })
                            .ToListAsync();

                        if (topArtistsForUser.Any())
                        {
                            this._cache.Set(key, topArtistsForUser, TimeSpan.FromHours(12));
                            topArtistsForEveryoneInServer.AddRange(topArtistsForUser);
                        }
                    }

                    
                }
            });

            await using var db = this._contextFactory.CreateDbContext();

            var userTopArtist = await db.UserArtists
                .AsQueryable()
                .OrderByDescending(o => o.Playcount)
                .FirstOrDefaultAsync(w => w.UserId == userId);

            var userAvgPlaycount = await db.UserArtists
                .AsQueryable()
                .Where(w => w.Playcount > 29 && w.UserId == userId)
                .AverageAsync(a => a.Playcount);

            var topArtists = await db.UserArtists
                .AsQueryable()
                .Where(
                    w => w.UserId == userId &&
                         w.Playcount > 29 &&
                         w.Name != null)
                .OrderByDescending(o => o.Playcount)
                .Select(s => new AffinityArtist
                {
                    ArtistName = s.Name.ToLower(),
                    Playcount = s.Playcount,
                    UserId = s.UserId,
                    Weight = ((decimal)s.Playcount / (decimal)userTopArtist.Playcount) * (s.Playcount > (userAvgPlaycount * 2) ? 24 : 8)
                })
                .ToListAsync();

            return topArtistsForEveryoneInServer
                .Where(w =>
                    w != null &&
                    topArtists.Select(s => s.ArtistName).Contains(w.ArtistName))
                .GroupBy(g => g.UserId)
                .OrderByDescending(g => g.Sum(s => s.Weight * topArtists.First(f => f.ArtistName == s.ArtistName).Weight))
                .Select(s => new AffinityArtistResultWithUser
                {
                    UserId = s.Key,
                    MatchPercentage = Math.Min(
                        ((decimal)s.Sum(w => w.Weight * topArtists.First(f => f.ArtistName == w.ArtistName).Weight)
                        / (decimal)topArtists.Sum(w => w.Weight) * 100) * 2, 100),
                    LastFMUsername = guildUsers.First(f => f.UserId == s.Key).UserNameLastFM,
                    Name = guildUsers.First(f => f.UserId == s.Key).UserNameLastFM
                })
                .ToList();
        }
    }
}
