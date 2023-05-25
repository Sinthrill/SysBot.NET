using Discord.Commands;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using PKHeX.Core;

namespace SysBot.Pokemon.Discord
{
    public class SudoModule<T> : ModuleBase<SocketCommandContext> where T : PKM, new()
    {
        [Command("cooldown")]
        [Summary("Changes cooldown in minutes.")]
        [RequireSudo]
        public async Task UpdateCooldown([Remainder] string input)
        {
            bool res = uint.TryParse(input, out var cooldown);
            if (res)
            {
                SysCordSettings.HubConfig.TradeAbuse.TradeCooldown = cooldown;
                SysCordSettings.HubConfig.TradeAbuse.CooldownUpdate = $"{DateTime.Now:yyyy.MM.dd - HH:mm:ss}";
                await ReplyAsync($"Cooldown has been updated to {cooldown} minutes.").ConfigureAwait(false);
                return;
            } else
            {
                await ReplyAsync("Please enter a valid number of minutes.").ConfigureAwait(false);
            }
        }

        [Command("wluser")]
        [Summary("Whitelist a user by NID. Format: <NID>, <Name>, <Duration:Days/Hours>, <Comment>")]
        [RequireSudo]
        public async Task WhitelistUser([Remainder] string input)
        {
            string msg = "", comment = "Default reasons";
            var argv = input.Split(", ");
            DateTime expires = DateTime.Now;           
            bool isValidID = ulong.TryParse(argv[0], out ulong trainerNID);
            if (!isValidID)
            {
                await ReplyAsync($"{argv[0]} is not a valid NID.").ConfigureAwait(false);
                return;
            }

            switch (argv.Length)
            {
                case 4:
                    var durArg = argv[2].Split(":", 2);
                    durArg[1] = durArg[1].ToLower();
                    comment = argv[3];
                    bool isValidDur = int.TryParse(durArg[0], out int duration);
                    if (!isValidDur)
                    {
                        msg += $"{durArg[0]} is an invalid number. Defaulting to no expiration.\r\n";
                        expires = DateTime.MaxValue;
                    } else
                    {
                        expires = durArg[1] switch
                        {
                            "days" => expires.AddDays(duration),
                            "hours" => expires.AddHours(duration),
                            _ => expires.AddDays(duration),
                        };
                    }
                    break;
                case 2:
                    break;
                default:
                    await ReplyAsync("Please use the proper format of <NID>, <Name>, <Duration:Days/Hours>, <Comment>").ConfigureAwait(false);
                    return;
            }

            string expiration = $"{expires:yyyy.MM.dd - HH:mm:ss}";
            var user = new RemoteControlAccess { ID = trainerNID, Name = argv[1], Expiration = expiration, Comment = comment};                

            SysCordSettings.HubConfig.TradeAbuse.WhitelistIDs.AddIfNew(new[] { user });
            msg += $"{user.Name}({user.ID}) has been added to the whitelist for \"{user.Comment}\".";
            await ReplyAsync(msg).ConfigureAwait(false);
        }

        [Command("bluser")]
        [Summary("Blacklist a user by NID. Format: <NID>, <Name>, <Duration:Days/Hours>, <Comment>")]
        [RequireSudo]
        public async Task BlacklistUser([Remainder] string input)
        {
            string msg = "", comment = "Default reasons";
            var argv = input.Split(", ");
            DateTime expires = DateTime.Now;
            bool isValidID = ulong.TryParse(argv[0], out ulong trainerNID);
            if (!isValidID)
            {
                await ReplyAsync($"{argv[0]} is not a valid NID.").ConfigureAwait(false);
                return;
            }

            switch (argv.Length)
            {
                case 4:
                    var durArg = argv[2].Split(":", 2);
                    durArg[1] = durArg[1].ToLower();
                    comment = argv[3];
                    bool isValidDur = int.TryParse(durArg[0], out int duration);
                    if (!isValidDur)
                    {
                        msg += $"{durArg[0]} is an invalid number. Defaulting to no expiration.\r\n";
                        expires = DateTime.MaxValue;
                    }
                    else
                    {
                        expires = durArg[1] switch
                        {
                            "days" => expires.AddDays(duration),
                            "hours" => expires.AddHours(duration),
                            _ => expires.AddDays(duration),
                        };
                    }
                    break;
                case 2:
                    break;
                default:
                    await ReplyAsync("Please use the proper format of <NID>, <Name>, <Duration:Days/Hours>, <Comment>").ConfigureAwait(false);
                    return;
            }

            string expiration = $"{expires:yyyy.MM.dd - HH:mm:ss}";
            var user = new RemoteControlAccess { ID = trainerNID, Name = argv[1], Expiration = expiration, Comment = comment };

            SysCordSettings.HubConfig.TradeAbuse.BannedIDs.AddIfNew(new[] { user });
            msg += $"{user.Name}({user.ID}) has been added to the blacklist for \"{user.Comment}\".";
            await ReplyAsync(msg).ConfigureAwait(false);
        }

        [Command("ledyspecies")]
        [Summary("Changes the Ledy species for idle distribution.")]
        [RequireSudo]
        public async Task ChangeLedySpecies([Remainder] string input)
        {
            if (input.ToLower() == "none")
            {
                SysCordSettings.HubConfig.Distribution.LedySpecies = (ushort)Species.None;
                await ReplyAsync("LedySpecies has been changed to None.");
                return;
            }
            bool isSpecies = Enum.TryParse(input, true, out Species result);            
            if (!isSpecies)
            {
                await ReplyAsync("Please enter a valid Species.").ConfigureAwait(false);
                return;
            }
            var mode = typeof(T).ToString();
            IPersonalTable infoTable = mode switch
            {
                "PK9" => PersonalTable.SV,
                "PK8" => PersonalTable.SWSH,
                "PA8" => PersonalTable.LA,
                "PB8" => PersonalTable.BDSP,
                _ => PersonalTable.SV
            };
            bool isInGame = infoTable.IsSpeciesInGame((ushort)result);
            if (!isInGame) 
            {
                await ReplyAsync("Please enter a Species available in the current game.").ConfigureAwait(false);
                return;
            } else {
                SysCordSettings.HubConfig.Distribution.LedySpecies = result;
                await ReplyAsync($"LedySpecies has been changed to {result}.");
            }
        }

        [Command("clonecode")]
        [Summary("Changes trade code for idle cloning.")]
        [RequireSudo]
        public async Task ChangeCloneCode([Remainder] string input)
        {
            bool res = int.TryParse(input, out var code);
            if (res)
            {
                SysCordSettings.HubConfig.Clone.TradeCode = code;
                await ReplyAsync($"Idle cloning code has been changed to {code}.").ConfigureAwait(false);
                return;
            } else
            {
                await ReplyAsync("Please enter a valid 1-8 digit number.").ConfigureAwait(false);
            }
        }

        [Command("distrocode")]
        [Summary("Changes trade code for idle distribution.")]
        [RequireSudo]
        public async Task ChangeDistroCode([Remainder] string input)
        {
            bool res = int.TryParse(input, out var code);
            if (res)
            {
                SysCordSettings.HubConfig.Distribution.TradeCode = code;
                await ReplyAsync($"Idle distribution code has been changed to {code}.").ConfigureAwait(false);
                return;
            } else
            {
                await ReplyAsync("Please enter a valid 1-8 digit number.").ConfigureAwait(false);
            }
        }

        [Command("whitelist")]
        [Summary("Whitelist an NID.")]
        [RequireSudo]
        public async Task WhiteListUser(string id, [Remainder] string name)
        {
            bool res = ulong.TryParse(id, out var userNID);
            RemoteControlAccess user = new()
            {
                ID = userNID,
                Name = name
            };
            if (res)
            {
                SysCordSettings.HubConfig.TradeAbuse.WhitelistIDs.AddIfNew(new[] { user });
                await ReplyAsync($"NID {userNID} has been whitelisted under name {name}").ConfigureAwait(false);
            }
        }

        [Command("blacklist")]
        [Summary("Blacklists a mentioned Discord user.")]
        [RequireSudo]
        // ReSharper disable once UnusedParameter.Global
        public async Task BlackListUsers([Remainder] string _)
        {
            var users = Context.Message.MentionedUsers;
            var objects = users.Select(GetReference);
            SysCordSettings.Settings.UserBlacklist.AddIfNew(objects);
            await ReplyAsync("Done.").ConfigureAwait(false);
        }

        [Command("blacklistComment")]
        [Summary("Adds a comment for a blacklisted Discord user ID.")]
        [RequireSudo]
        // ReSharper disable once UnusedParameter.Global
        public async Task BlackListUsers(ulong id, [Remainder] string comment)
        {
            var obj = SysCordSettings.Settings.UserBlacklist.List.Find(z => z.ID == id);
            if (obj is null)
            {
                await ReplyAsync($"Unable to find a user with that ID ({id}).").ConfigureAwait(false);
                return;
            }

            var oldComment = obj.Comment;
            obj.Comment = comment;
            await ReplyAsync($"Done. Changed existing comment ({oldComment}) to ({comment}).").ConfigureAwait(false);
        }

        [Command("unblacklist")]
        [Summary("Removes a mentioned Discord user from the blacklist.")]
        [RequireSudo]
        // ReSharper disable once UnusedParameter.Global
        public async Task UnBlackListUsers([Remainder] string _)
        {
            var users = Context.Message.MentionedUsers;
            var objects = users.Select(GetReference);
            SysCordSettings.Settings.UserBlacklist.RemoveAll(z => objects.Any(o => o.ID == z.ID));
            await ReplyAsync("Done.").ConfigureAwait(false);
        }

        [Command("blacklistId")]
        [Summary("Blacklists Discord user IDs. (Useful if user is not in the server).")]
        [RequireSudo]
        public async Task BlackListIDs([Summary("Comma Separated Discord IDs")][Remainder] string content)
        {
            var IDs = GetIDs(content);
            var objects = IDs.Select(GetReference);
            SysCordSettings.Settings.UserBlacklist.AddIfNew(objects);
            await ReplyAsync("Done.").ConfigureAwait(false);
        }

        [Command("unBlacklistId")]
        [Summary("Removes Discord user IDs from the blacklist. (Useful if user is not in the server).")]
        [RequireSudo]
        public async Task UnBlackListIDs([Summary("Comma Separated Discord IDs")][Remainder] string content)
        {
            var IDs = GetIDs(content);
            SysCordSettings.Settings.UserBlacklist.RemoveAll(z => IDs.Any(o => o == z.ID));
            await ReplyAsync("Done.").ConfigureAwait(false);
        }

        [Command("blacklistSummary")]
        [Alias("printBlacklist", "blacklistPrint")]
        [Summary("Prints the list of blacklisted Discord users.")]
        [RequireSudo]
        public async Task PrintBlacklist()
        {
            var lines = SysCordSettings.Settings.UserBlacklist.Summarize();
            var msg = string.Join("\n", lines);
            await ReplyAsync(Format.Code(msg)).ConfigureAwait(false);
        }

        [Command("banID")]
        [Summary("Bans online user IDs.")]
        [RequireSudo]
        public async Task BanOnlineIDs([Summary("Comma Separated Online IDs")][Remainder] string content)
        {
            var IDs = GetIDs(content);
            var objects = IDs.Select(GetReference);

            var me = SysCord<T>.Runner;
            var hub = me.Hub;
            hub.Config.TradeAbuse.BannedIDs.AddIfNew(objects);
            await ReplyAsync("Done.").ConfigureAwait(false);
        }

        [Command("bannedIDComment")]
        [Summary("Adds a comment for a banned online user ID.")]
        [RequireSudo]
        public async Task BanOnlineIDs(ulong id, [Remainder] string comment)
        {
            var me = SysCord<T>.Runner;
            var hub = me.Hub;
            var obj = hub.Config.TradeAbuse.BannedIDs.List.Find(z => z.ID == id);
            if (obj is null)
            {
                await ReplyAsync($"Unable to find a user with that online ID ({id}).").ConfigureAwait(false);
                return;
            }

            var oldComment = obj.Comment;
            obj.Comment = comment;
            await ReplyAsync($"Done. Changed existing comment ({oldComment}) to ({comment}).").ConfigureAwait(false);
        }

        [Command("unbanID")]
        [Summary("Bans online user IDs.")]
        [RequireSudo]
        public async Task UnBanOnlineIDs([Summary("Comma Separated Online IDs")][Remainder] string content)
        {
            var IDs = GetIDs(content);
            var objects = IDs.Select(GetReference);

            var me = SysCord<T>.Runner;
            var hub = me.Hub;
            hub.Config.TradeAbuse.BannedIDs.RemoveAll(z => IDs.Any(o => o == z.ID));
            await ReplyAsync("Done.").ConfigureAwait(false);
        }

        [Command("bannedIDSummary")]
        [Alias("printBannedID", "bannedIDPrint")]
        [Summary("Prints the list of banned online IDs.")]
        [RequireSudo]
        public async Task PrintBannedOnlineIDs()
        {
            var me = SysCord<T>.Runner;
            var hub = me.Hub;
            var lines = hub.Config.TradeAbuse.BannedIDs.Summarize();
            var msg = string.Join("\n", lines);
            await ReplyAsync(Format.Code(msg)).ConfigureAwait(false);
        }

        [Command("forgetUser")]
        [Alias("forget")]
        [Summary("Forgets users that were previously encountered.")]
        [RequireSudo]
        public async Task ForgetPreviousUser([Summary("Comma Separated Online IDs")][Remainder] string content)
        {
            var IDs = GetIDs(content);
            var objects = IDs.Select(GetReference);

            foreach (var ID in IDs)
            {
                PokeRoutineExecutorBase.PreviousUsers.RemoveAll(ID);
                PokeRoutineExecutorBase.PreviousUsersDistribution.RemoveAll(ID);
            }
            await ReplyAsync("Done.").ConfigureAwait(false);
        }

        [Command("previousUserSummary")]
        [Alias("prevUsers")]
        [Summary("Prints a list of previously encountered users.")]
        [RequireSudo]
        public async Task PrintPreviousUsers()
        {
            bool found = false;
            var lines = PokeRoutineExecutorBase.PreviousUsers.Summarize();
            if (lines.Any())
            {
                found = true;
                var msg = "Previous Users:\n" + string.Join("\n", lines);
                await ReplyAsync(Format.Code(msg)).ConfigureAwait(false);
            }

            lines = PokeRoutineExecutorBase.PreviousUsersDistribution.Summarize();
            if (lines.Any())
            {
                found = true;
                var msg = "Previous Distribution Users:\n" + string.Join("\n", lines);
                await ReplyAsync(Format.Code(msg)).ConfigureAwait(false);
            }
            if (!found)
                await ReplyAsync("No previous users found.").ConfigureAwait(false);
        }

        private RemoteControlAccess GetReference(IUser channel) => new()
        {
            ID = channel.Id,
            Name = channel.Username,
            Comment = $"Added by {Context.User.Username} on {DateTime.Now:yyyy.MM.dd-hh:mm:ss}",
        };

        private RemoteControlAccess GetReference(ulong id) => new()
        {
            ID = id,
            Name = "Manual",
            Comment = $"Added by {Context.User.Username} on {DateTime.Now:yyyy.MM.dd-hh:mm:ss}",
        };

        protected static IEnumerable<ulong> GetIDs(string content)
        {
            return content.Split(new[] { ",", ", ", " " }, StringSplitOptions.RemoveEmptyEntries)
                .Select(z => ulong.TryParse(z, out var x) ? x : 0).Where(z => z != 0);
        }
    }
}