using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using HPGMissionCompanion;

internal static class RuntimePayloadTests
{
    internal static void Run(Action<bool, string> require)
    {
        // Exercise the actual IPC deserializer without creating Worker, Player or a device.
        var payload = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
            "{\"RecordedMuted\":[\"L:ONE\"],\"Paths\":[\"pack.xml\"],\"Actors\":[{\"Pool\":[\"voice-a\",\"voice-b\"]}]}");
        require((string)RuntimePayload.Array(payload, "RecordedMuted")[0] == "L:ONE", "IPC mute arrays accept JavaScriptSerializer lists");
        require((string)RuntimePayload.Array(payload, "Paths")[0] == "pack.xml", "IPC registration paths accept JavaScriptSerializer lists");
        var actor = (Dictionary<string, object>)RuntimePayload.Array(payload, "Actors")[0];
        require(RuntimePayload.Array(actor, "Pool").Length == 2, "IPC actor pools retain nested array values");
        require(RuntimePayload.Array(payload, "Missing").Length == 0, "Absent optional IPC arrays remain empty");
        payload["Paths"] = new object[] { "existing.xml" };
        require(RuntimePayload.Array(payload, "Paths").Length == 1, "IPC object arrays remain supported");
        payload["Paths"] = "not an array";
        var rejected = false;
        try { RuntimePayload.Array(payload, "Paths"); } catch (InvalidDataException) { rejected = true; }
        require(rejected, "IPC arrays reject scalar strings instead of silently dropping edits");
    }
}
