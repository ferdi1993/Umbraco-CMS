using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Umbraco.Core.Cache;
using Umbraco.Core.IO;
using Umbraco.Core.Logging;
using Umbraco.Core.Models.Rdbms;
using Umbraco.Core.Persistence;
using umbraco.interfaces;
using Umbraco.Core.Persistence.SqlSyntax;

namespace Umbraco.Core.Sync
{
    /// <summary>
    /// An <see cref="IServerMessenger"/> that works by storing messages in the database.
    /// </summary>
    //
    // this messenger writes ALL instructions to the database,
    // but only processes instructions coming from remote servers,
    // thus ensuring that instructions run only once
    //
    public class DatabaseServerMessenger : ServerMessengerBase
    {
        private readonly ApplicationContext _appContext;        
        private readonly ManualResetEvent _syncIdle;
        private readonly object _locko = new object();
        private readonly ILogger _logger;
        private int _lastId = -1;
        private DateTime _lastSync;
        private DateTime _lastPruned;
        private bool _initialized;
        private bool _syncing;
        private bool _released;
        private readonly ProfilingLogger _profilingLogger;

        protected DatabaseServerMessengerOptions Options { get; private set; }
        protected ApplicationContext ApplicationContext { get { return _appContext; } }

        public DatabaseServerMessenger(ApplicationContext appContext, bool distributedEnabled, DatabaseServerMessengerOptions options)
            : base(distributedEnabled)
        {
            if (appContext == null) throw new ArgumentNullException("appContext");
            if (options == null) throw new ArgumentNullException("options");

            _appContext = appContext;
            Options = options;
            _lastPruned = _lastSync = DateTime.UtcNow;
            _syncIdle = new ManualResetEvent(true);
            _profilingLogger = appContext.ProfilingLogger;
            _logger = appContext.ProfilingLogger.Logger;
        }

        #region Messenger

        protected override bool RequiresDistributed(IEnumerable<IServerAddress> servers, ICacheRefresher refresher, MessageType dispatchType)
        {
            // we don't care if there's servers listed or not,
            // if distributed call is enabled we will make the call
            return _initialized && DistributedEnabled;
        }

        protected override void DeliverRemote(
            IEnumerable<IServerAddress> servers,
            ICacheRefresher refresher,
            MessageType messageType,
            IEnumerable<object> ids = null,
            string json = null)
        {
            var idsA = ids == null ? null : ids.ToArray();

            Type idType;
            if (GetArrayType(idsA, out idType) == false)
                throw new ArgumentException("All items must be of the same type, either int or Guid.", "ids");

            var instructions = RefreshInstruction.GetInstructions(refresher, messageType, idsA, idType, json);

            var dto = new CacheInstructionDto
            {
                UtcStamp = DateTime.UtcNow,
                Instructions = JsonConvert.SerializeObject(instructions, Formatting.None),
                OriginIdentity = LocalIdentity
            };

            ApplicationContext.DatabaseContext.Database.Insert(dto);
        }

        #endregion

        #region Sync

        /// <summary>
        /// Boots the messenger.
        /// </summary>
        /// <remarks>
        /// Thread safety: this is NOT thread safe. Because it is NOT meant to run multi-threaded.
        /// Callers MUST ensure thread-safety.
        /// </remarks>
        protected void Boot()
        {
            // weight:10, must release *before* the facade service, because once released
            // the service will *not* be able to properly handle our notifications anymore
            const int weight = 10;

            var registered = ApplicationContext.MainDom.Register(
                () =>
                {
                    lock (_locko)
                    {
                        _released = true; // no more syncs
                    }

                    // Wait for pending sync this is waiting for _syncIdle.Set()
                    // to be called. Until that is called, the appdomain cannot shut down!
                    // so whatever is locking this currently should hurry up!
                    _syncIdle.WaitOne(); 
                },
                weight);

            if (registered == false)
                return;

            ReadLastSynced(); // get _lastId
            EnsureInstructions(); // reset _lastId if instrs are missing
            Initialize(); // boot
        }

        /// <summary>
        /// Initializes a server that has never synchronized before.
        /// </summary>
        /// <remarks>
        /// Thread safety: this is NOT thread safe. Because it is NOT meant to run multi-threaded.
        /// Callers MUST ensure thread-safety.
        /// </remarks>
        private void Initialize()
        {
            lock (_locko)
            {
                if (_released) return;

                var coldboot = false;
                if (_lastId < 0) // never synced before
                {
                    // we haven't synced - in this case we aren't going to sync the whole thing, we will assume this is a new
                    // server and it will need to rebuild it's own caches, eg Lucene or the xml cache file.
                    _logger.Warn<DatabaseServerMessenger>("No last synced Id found, this generally means this is a new server/install."
                        + " The server will build its caches and indexes, and then adjust its last synced Id to the latest found in"
                        + " the database and maintain cache updates based on that Id.");

                    coldboot = true;
                }
                else
                {
                    //check for how many instructions there are to process
                    var count = _appContext.DatabaseContext.Database.ExecuteScalar<int>("SELECT COUNT(*) FROM umbracoCacheInstruction WHERE id > @lastId", new {lastId = _lastId});
                    if (count > Options.MaxProcessingInstructionCount)
                    {
                        //too many instructions, proceed to cold boot
                        _logger.Warn<DatabaseServerMessenger>("The instruction count ({0}) exceeds the specified MaxProcessingInstructionCount ({1})."
                            + " The server will skip existing instructions, rebuild its caches and indexes entirely, adjust its last synced Id"
                            + " to the latest found in the database and maintain cache updates based on that Id.",
                            () => count, () => Options.MaxProcessingInstructionCount);

                        coldboot = true;
                    }
                }

                if (coldboot)
                {
                    // go get the last id in the db and store it
                    // note: do it BEFORE initializing otherwise some instructions might get lost
                    // when doing it before, some instructions might run twice - not an issue
                    var maxId = _appContext.DatabaseContext.Database.ExecuteScalar<int>("SELECT MAX(id) FROM umbracoCacheInstruction");

                    //if there is a max currently, or if we've never synced
                    if (maxId > 0 || _lastId < 0)
                        SaveLastSynced(maxId);

                    // execute initializing callbacks
                    if (Options.InitializingCallbacks != null)
                        foreach (var callback in Options.InitializingCallbacks)
                            callback();
                }

                _initialized = true;
            }
        }

        /// <summary>
        /// Synchronize the server (throttled).
        /// </summary>
        protected void Sync()
        {
            lock (_locko)
            {
                if (_syncing)
                    return;
                
                //Don't continue if we are released
                if (_released)
                    return;

                if ((DateTime.UtcNow - _lastSync).TotalSeconds <= Options.ThrottleSeconds)
                    return;

                //Set our flag and the lock to be in it's original state (i.e. it can be awaited)
                _syncing = true;
                _syncIdle.Reset();
                _lastSync = DateTime.UtcNow;
            }

            try
            {
                using (_profilingLogger.DebugDuration<DatabaseServerMessenger>("Syncing from database..."))
                {
                    ProcessDatabaseInstructions();

                    //Check for pruning throttling
                    if ((DateTime.UtcNow - _lastPruned).TotalSeconds <= Options.PruneThrottleSeconds)
                        return;

                    _lastPruned = _lastSync;

                    switch (_appContext.GetCurrentServerRole())
                    {
                        case ServerRole.Single:
                        case ServerRole.Master:
                            PruneOldInstructions();
                            break;
                    }
                }
            }
            finally
            {
                //We must reset our flag and signal any waiting locks
                _syncing = false;
                _syncIdle.Set();
            }
        }

        /// <summary>
        /// Process instructions from the database.
        /// </summary>
        /// <remarks>
        /// Thread safety: this is NOT thread safe. Because it is NOT meant to run multi-threaded.
        /// </remarks>
        /// <returns>
        /// Returns the number of processed instructions
        /// </returns>
        private void ProcessDatabaseInstructions()
        {
            // NOTE
            // we 'could' recurse to ensure that no remaining instructions are pending in the table before proceeding but I don't think that
            // would be a good idea since instructions could keep getting added and then all other threads will probably get stuck from serving requests
            // (depending on what the cache refreshers are doing). I think it's best we do the one time check, process them and continue, if there are
            // pending requests after being processed, they'll just be processed on the next poll.
            //
            // FIXME not true if we're running on a background thread, assuming we can?

            
            var sql = new Sql().Select("*")
                .From<CacheInstructionDto>(_appContext.DatabaseContext.SqlSyntax)
                .Where<CacheInstructionDto>(dto => dto.Id > _lastId)
                .OrderBy<CacheInstructionDto>(dto => dto.Id, _appContext.DatabaseContext.SqlSyntax);

            //only retrieve the max (just in case there's tons)
            var topSql = _appContext.DatabaseContext.SqlSyntax.SelectTop(sql, Options.MaxProcessingInstructionCount);

            // only process instructions coming from a remote server, and ignore instructions coming from
            // the local server as they've already been processed. We should NOT assume that the sequence of
            // instructions in the database makes any sense whatsoever, because it's all async.
            var localIdentity = LocalIdentity;

            var lastId = 0;

            //this is used to determine if we need to exit the reader loop below because there are actually
            // too many instructions to process. In which case we need to exit the reader so we can actually re-save
            // the remaining instructions back to the same row (we cannot save something while inside a reader loop)
            Tuple<CacheInstructionDto, List<RefreshInstruction>> maxInstructions = null;

            //IMPORTANT! We are doing a Query here instead of a Fetch, this means that it will open a data reader
            // which we are iterating over instead of loading everything into memory and iterating over that.
            // When doing this we always must use a for loop so that the Enumerator is disposed and the reader is closed.
            foreach (var dto in _appContext.DatabaseContext.Database.Query<CacheInstructionDto>(topSql))
            {
                if (dto.OriginIdentity == localIdentity)
                {
                    // just skip that local one but update lastId nevertheless
                    lastId = dto.Id;
                    continue;
                }

                // deserialize remote instructions & skip if it fails
                JArray jsonA;
                try
                {
                    jsonA = JsonConvert.DeserializeObject<JArray>(dto.Instructions);
                }
                catch (JsonException ex)
                {
                    _logger.Error<DatabaseServerMessenger>(string.Format("Failed to deserialize instructions ({0}: \"{1}\").", dto.Id, dto.Instructions), ex);
                    lastId = dto.Id; // skip
                    continue;
                }

                var instructionBatch = GetAllInstructions(jsonA);

                // Here we should check if there's too many instructions, if there is we should split them and re-save the instructions entry with
                // the trimmed instructions. We then don't update the lastsynced value so that this row is re-processed again but with only the remaining
                // instructions in it.
                if (instructionBatch.Count > Options.MaxProcessingInstructionCount)
                {
                    maxInstructions = new Tuple<CacheInstructionDto, List<RefreshInstruction>>(dto, instructionBatch);
                    break;                    
                }

                //process as per-normal
                lastId = ProcessDatabaseInstructions(instructionBatch, dto);
            }

            //If this is not null this means we've found a row that has a ton of instructions in it and we'll need to process
            // just a part of it and then re-save the remaining to the same row so that another request can deal with the data.
            if (maxInstructions != null)
            {
                var remainingCount = maxInstructions.Item2.Count - Options.MaxProcessingInstructionCount;

                _logger.Info<DatabaseServerMessenger>(
                    "Max processing instruction count reached. This batch will be processed now but the remaining {0} will be processed by subsequent requests.", () => remainingCount);

                var processingBatch = maxInstructions.Item2.GetRange(0, Options.MaxProcessingInstructionCount);
                //NOTE: We are not persisting the lastId from the result of this method because we will need to re-process it
                ProcessDatabaseInstructions(processingBatch, maxInstructions.Item1);

                //Save the instruction blob back to the DB with the trimmed instruction count
                var remaining = maxInstructions.Item2.GetRange(Options.MaxProcessingInstructionCount - 1, remainingCount);
                maxInstructions.Item1.UtcStamp = DateTime.UtcNow;
                //serialize the remaining instructions (leave the original identity as-is)
                maxInstructions.Item1.Instructions = JsonConvert.SerializeObject(remaining, Formatting.None);
                ApplicationContext.DatabaseContext.Database.Update(maxInstructions.Item1);                
            }

            if (lastId > 0)
                SaveLastSynced(lastId);
        }

        private int ProcessDatabaseInstructions(List<RefreshInstruction> instructionBatch, CacheInstructionDto dto)
        {
            // execute remote instructions & update lastId
            try
            {
                NotifyRefreshers(instructionBatch);
                return dto.Id;
            }
            //catch (ThreadAbortException ex)
            //{
            //    //This will occur if the instructions processing is taking too long since this is occuring on a request thread.
            //    // Or possibly if IIS terminates the appdomain. In any case, we should deal with this differently perhaps...
            //}
            catch (Exception ex)
            {
                _logger.Error<DatabaseServerMessenger>(
                    string.Format("DISTRIBUTED CACHE IS NOT UPDATED. Failed to execute instructions (id: {0}, instruction count: {1}). Instruction is being skipped/ignored", dto.Id, instructionBatch.Count), ex);

                //we cannot throw here because this invalid instruction will just keep getting processed over and over and errors
                // will be thrown over and over. The only thing we can do is ignore and move on.
                return dto.Id;
            }

            ////if this is returned it will not be saved
            //return -1;
        }

        /// <summary>
        /// Remove old instructions from the database
        /// </summary>
        /// <remarks>
        /// Always leave the last (most recent) record in the db table, this is so that not all instructions are removed which would cause
        /// the site to cold boot if there's been no instruction activity for more than DaysToRetainInstructions.
        /// See: http://issues.umbraco.org/issue/U4-7643#comment=67-25085
        /// </remarks>
        private void PruneOldInstructions()
        {
            var pruneDate = DateTime.UtcNow.AddDays(-Options.DaysToRetainInstructions);

            // using 2 queries is faster than convoluted joins

            var maxId = _appContext.DatabaseContext.Database.ExecuteScalar<int>("SELECT MAX(id) FROM umbracoCacheInstruction;");

            var delete = new Sql().Append(@"DELETE FROM umbracoCacheInstruction WHERE utcStamp < @pruneDate AND id < @maxId",
                new { pruneDate, maxId });

            _appContext.DatabaseContext.Database.Execute(delete);
        }

        /// <summary>
        /// Ensure that the last instruction that was processed is still in the database.
        /// </summary>
        /// <remarks>
        /// If the last instruction is not in the database anymore, then the messenger
        /// should not try to process any instructions, because some instructions might be lost,
        /// and it should instead cold-boot.
        /// However, if the last synced instruction id is '0' and there are '0' records, then this indicates
        /// that it's a fresh site and no user actions have taken place, in this circumstance we do not want to cold
        /// boot. See: http://issues.umbraco.org/issue/U4-8627
        /// </remarks>
        private void EnsureInstructions()
        {
            if (_lastId == 0)
            {
                var sql = new Sql().Select("COUNT(*)")
                    .From<CacheInstructionDto>(_appContext.DatabaseContext.SqlSyntax);

                var count = _appContext.DatabaseContext.Database.ExecuteScalar<int>(sql);

                //if there are instructions but we haven't synced, then a cold boot is necessary
                if (count > 0)
                    _lastId = -1;
            }
            else
            {
                var sql = new Sql().Select("*")
                .From<CacheInstructionDto>(_appContext.DatabaseContext.SqlSyntax)
                .Where<CacheInstructionDto>(dto => dto.Id == _lastId);

                var dtos = _appContext.DatabaseContext.Database.Fetch<CacheInstructionDto>(sql);

                //if the last synced instruction is not found in the db, then a cold boot is necessary
                if (dtos.Count == 0)
                    _lastId = -1;
            }
        }

        /// <summary>
        /// Reads the last-synced id from file into memory.
        /// </summary>
        /// <remarks>
        /// Thread safety: this is NOT thread safe. Because it is NOT meant to run multi-threaded.
        /// </remarks>
        private void ReadLastSynced()
        {
            var path = SyncFilePath;
            if (File.Exists(path) == false) return;

            var content = File.ReadAllText(path);
            int last;
            if (int.TryParse(content, out last))
                _lastId = last;
        }

        /// <summary>
        /// Updates the in-memory last-synced id and persists it to file.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <remarks>
        /// Thread safety: this is NOT thread safe. Because it is NOT meant to run multi-threaded.
        /// </remarks>
        private void SaveLastSynced(int id)
        {
            File.WriteAllText(SyncFilePath, id.ToString(CultureInfo.InvariantCulture));
            _lastId = id;
        }

        /// <summary>
        /// Gets the unique local identity of the executing AppDomain.
        /// </summary>
        /// <remarks>
        /// <para>It is not only about the "server" (machine name and appDomainappId), but also about
        /// an AppDomain, within a Process, on that server - because two AppDomains running at the same
        /// time on the same server (eg during a restart) are, practically, a LB setup.</para>
        /// <para>Practically, all we really need is the guid, the other infos are here for information
        /// and debugging purposes.</para>
        /// </remarks>
        protected static readonly string LocalIdentity = NetworkHelper.MachineName // eg DOMAIN\SERVER
            + "/" + HttpRuntime.AppDomainAppId // eg /LM/S3SVC/11/ROOT
            + " [P" + Process.GetCurrentProcess().Id // eg 1234
            + "/D" + AppDomain.CurrentDomain.Id // eg 22
            + "] " + Guid.NewGuid().ToString("N").ToUpper(); // make it truly unique

        /// <summary>
        /// Gets the sync file path for the local server.
        /// </summary>
        /// <returns>The sync file path for the local server.</returns>
        private static string SyncFilePath
        {
            get
            {
                var tempFolder = IOHelper.MapPath("~/App_Data/TEMP/DistCache/" + NetworkHelper.FileSafeMachineName);
                if (Directory.Exists(tempFolder) == false)
                    Directory.CreateDirectory(tempFolder);

                return Path.Combine(tempFolder, HttpRuntime.AppDomainAppId.ReplaceNonAlphanumericChars(string.Empty) + "-lastsynced.txt");
            }
        }

        #endregion

        #region Notify refreshers

        private static ICacheRefresher GetRefresher(Guid id)
        {
            var refresher = CacheRefreshersResolver.Current.GetById(id);
            if (refresher == null)
                throw new InvalidOperationException("Cache refresher with ID \"" + id + "\" does not exist.");
            return refresher;
        }

        private static IJsonCacheRefresher GetJsonRefresher(Guid id)
        {
            return GetJsonRefresher(GetRefresher(id));
        }

        private static IJsonCacheRefresher GetJsonRefresher(ICacheRefresher refresher)
        {
            var jsonRefresher = refresher as IJsonCacheRefresher;
            if (jsonRefresher == null)
                throw new InvalidOperationException("Cache refresher with ID \"" + refresher.UniqueIdentifier + "\" does not implement " + typeof(IJsonCacheRefresher) + ".");
            return jsonRefresher;
        }

        /// <summary>
        /// Parses out the individual instructions to be processed
        /// </summary>
        /// <param name="jsonArray"></param>
        /// <returns></returns>
        private static List<RefreshInstruction> GetAllInstructions(IEnumerable<JToken> jsonArray)
        {
            var result = new List<RefreshInstruction>();
            foreach (var jsonItem in jsonArray)
            {
                // could be a JObject in which case we can convert to a RefreshInstruction,
                // otherwise it could be another JArray - in which case we'll iterate that.
                var jsonObj = jsonItem as JObject;
                if (jsonObj != null)
                {
                    var instruction = jsonObj.ToObject<RefreshInstruction>();
                    result.Add(instruction);
                }
                else
                {
                    var jsonInnerArray = (JArray)jsonItem;
                    result.AddRange(GetAllInstructions(jsonInnerArray)); // recurse
                }
            }
            return result;
        }

        /// <summary>
        /// executes the instructions against the cache refresher instances
        /// </summary>
        /// <param name="instructions"></param>
        private static void NotifyRefreshers(IEnumerable<RefreshInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                switch (instruction.RefreshType)
                {
                    case RefreshMethodType.RefreshAll:
                        RefreshAll(instruction.RefresherId);
                        break;
                    case RefreshMethodType.RefreshByGuid:
                        RefreshByGuid(instruction.RefresherId, instruction.GuidId);
                        break;
                    case RefreshMethodType.RefreshById:
                        RefreshById(instruction.RefresherId, instruction.IntId);
                        break;
                    case RefreshMethodType.RefreshByIds:
                        RefreshByIds(instruction.RefresherId, instruction.JsonIds);
                        break;
                    case RefreshMethodType.RefreshByJson:
                        RefreshByJson(instruction.RefresherId, instruction.JsonPayload);
                        break;
                    case RefreshMethodType.RemoveById:
                        RemoveById(instruction.RefresherId, instruction.IntId);
                        break;
                }
            }
        }

        private static void RefreshAll(Guid uniqueIdentifier)
        {
            var refresher = GetRefresher(uniqueIdentifier);
            refresher.RefreshAll();
        }

        private static void RefreshByGuid(Guid uniqueIdentifier, Guid id)
        {
            var refresher = GetRefresher(uniqueIdentifier);
            refresher.Refresh(id);
        }

        private static void RefreshById(Guid uniqueIdentifier, int id)
        {
            var refresher = GetRefresher(uniqueIdentifier);
            refresher.Refresh(id);
        }

        private static void RefreshByIds(Guid uniqueIdentifier, string jsonIds)
        {
            var refresher = GetRefresher(uniqueIdentifier);
            foreach (var id in JsonConvert.DeserializeObject<int[]>(jsonIds))
                refresher.Refresh(id);
        }

        private static void RefreshByJson(Guid uniqueIdentifier, string jsonPayload)
        {
            var refresher = GetJsonRefresher(uniqueIdentifier);
            refresher.Refresh(jsonPayload);
        }

        private static void RemoveById(Guid uniqueIdentifier, int id)
        {
            var refresher = GetRefresher(uniqueIdentifier);
            refresher.Remove(id);
        }

        #endregion
    }
}
