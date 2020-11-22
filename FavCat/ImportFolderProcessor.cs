using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FavCat.Database.Stored;
using LiteDB;
using MelonLoader;
using VRC.Core;
using Random = UnityEngine.Random;

namespace FavCat
{
    public static class ImportFolderProcessor
    {
        public static bool ImportRunning { get; private set; }
        
        public static async Task ProcessImportsFolder()
        {
            ImportRunning = true;
            
            var databases = new List<string>();
            var textFiles = new List<string>();
            foreach (var file in Directory.EnumerateFiles("./UserData/FavCatImport"))
            {
                if (file.EndsWith(".db")) 
                    databases.Add(file);
                else
                    textFiles.Add(file);
            }
            
            foreach (var file in databases)
            {
                try
                {
                    await MergeInForeignStore(file);
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    MelonLogger.Log($"Import of {file} failed: {ex}");
                }
            }
            
            foreach (var textFile in textFiles)
            {
                try
                {
                    await ProcessTextFile(textFile);
                }
                catch (Exception ex)
                {
                    MelonLogger.Log($"Import of {textFile} failed: {ex}");
                }
            }

            ImportRunning = false;
        }
        
        private static readonly Regex AvatarIdRegex = new Regex("avtr_[0-9a-fA-F]{8}\\-[0-9a-fA-F]{4}\\-[0-9a-fA-F]{4}\\-[0-9a-fA-F]{4}\\-[0-9a-fA-F]{12}");

        internal static async Task ProcessTextFile(string filePath)
        {
            var fileName = Path.GetFileName(filePath);
            MelonLogger.Log($"Started avatar import process for file {fileName}");
            
            var toAdd = new List<string>();
            { // file access block
                using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                using var reader = new StreamReader(file);
                string line;
                
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    var matches = AvatarIdRegex.Matches(line);
                    foreach (Match match in matches)
                    {
                        var avatarId = match.Value;
                        toAdd.Add(avatarId);
                        if (FavCatMod.Database.myStoredAvatars.FindById(avatarId) == null)
                        {
                            await FavCatMod.YieldToMainThread();
                            new ApiAvatar {id = avatarId}.Fetch(); // it will get intercepted and stored
                            await Task.Delay(TimeSpan.FromSeconds(10f + Random.Range(5f, 10f))).ConfigureAwait(false);
                        }
                    }
                }
            }

            await FavCatMod.YieldToMainThread();
            var userId = APIUser.CurrentUser.id;
            var categoryName = $"Imported from {fileName}";
            var existingCategory = FavCatMod.Database.AvatarFavorites.GetCategory(categoryName);
            foreach (var avatarId in toAdd)
            {
                if (FavCatMod.Database.AvatarFavorites.IsFavorite(avatarId, categoryName))
                    continue;
                
                var storedAvatar = FavCatMod.Database.myStoredAvatars.FindById(avatarId);
                if (storedAvatar == null || storedAvatar.ReleaseStatus != "public" && storedAvatar.AuthorId != userId)
                    continue;
                
                FavCatMod.Database.AvatarFavorites.AddFavorite(avatarId, categoryName);
            }
            
            if (existingCategory == null)
            {
                existingCategory = new StoredCategory {CategoryName = categoryName, SortType = "!added"};
                FavCatMod.Database.AvatarFavorites.UpdateCategory(existingCategory);

                var avatarModule = FavCatMod.Instance.AvatarModule!;
                avatarModule.CreateList(existingCategory);
                avatarModule.ReorderLists();
                avatarModule.RefreshFavButtons();
            }
            
            MelonLogger.Log($"Done importing {fileName}");
            File.Delete(filePath);
        }
        
        internal static Task MergeInForeignStore(string foreignStorePath)
        {
            return Task.Run(() =>
            {
                var fileName = Path.GetFileName(foreignStorePath);
                MelonLogger.Log($"Started merging database with {fileName}");
                using var storeDatabase = new LiteDatabase(new ConnectionString {Filename = foreignStorePath, ReadOnly = true, Connection = ConnectionType.Direct});
            
                var storedAvatars = storeDatabase.GetCollection<StoredAvatar>("avatars");
                var storedPlayers = storeDatabase.GetCollection<StoredPlayer>("players");
                var storedWorlds = storeDatabase.GetCollection<StoredWorld>("worlds");
                
                foreach (var storedAvatar in storedAvatars.FindAll())
                {
                    var existingStored = FavCatMod.Database.myStoredAvatars.FindById(storedAvatar.AvatarId);
                    if (existingStored == null || existingStored.UpdatedAt < storedAvatar.UpdatedAt)
                        FavCatMod.Database.myStoredAvatars.Upsert(storedAvatar);
                }
                
                foreach (var storedPlayer in storedPlayers.FindAll())
                {
                    var existingStored = FavCatMod.Database.myStoredPlayers.FindById(storedPlayer.PlayerId);
                    if (existingStored == null)
                        FavCatMod.Database.myStoredPlayers.Upsert(storedPlayer);
                }
                
                foreach (var storedWorld in storedWorlds.FindAll())
                {
                    var existingStored = FavCatMod.Database.myStoredWorlds.FindById(storedWorld.WorldId);
                    if (existingStored == null || existingStored.UpdatedAt < storedWorld.UpdatedAt)
                        FavCatMod.Database.myStoredWorlds.Upsert(storedWorld);
                }
                
                MelonLogger.Log($"Done merging database with {fileName}");
            });
        }
    }
}