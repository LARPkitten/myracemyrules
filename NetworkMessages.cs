using ProtoBuf;

namespace MyRaceMyRules
{
    /// <summary>
    /// Server -> client config sync (design 2b).
    ///
    /// Flow:
    ///   - On client join, the server sends <see cref="ConfigSyncPacket"/> with the
    ///     authoritative config JSON and a content hash.
    ///   - The client compares the hash to what it APPLIED this session (recorded when it
    ///     mutated assets at load). If they match, nothing to do. If they differ, the client
    ///     overwrites its local server-cache file and asks the player to reconnect so the new
    ///     values apply at the next load (PlayerModelLib reads model configs at load only).
    ///
    /// There is no client -> server message: clients never edit. The server config file is the
    /// sole source of truth; the client cache is a read-only mirror the server overwrites on
    /// every join.
    /// </summary>
    [ProtoContract]
    public class ConfigSyncPacket
    {
        /// <summary>Authoritative config, serialized as JSON (Newtonsoft).</summary>
        [ProtoMember(1)]
        public string ConfigJson = "";

        /// <summary>Stable hash of the config, used to detect "changed since I applied it".</summary>
        [ProtoMember(2)]
        public string ConfigHash = "";
    }
}
