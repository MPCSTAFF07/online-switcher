using System.Text.Json.Nodes;
using SIPSorcery.Net;

public static class IceSignaling
{
    public static JsonObject ForBrowser(RTCIceCandidate candidate)
    {
        // candidate.candidate is the raw SDP value in SIPSorcery. toJSON()
        // adds the required "candidate:" prefix and supplies a valid sdpMid.
        return JsonNode.Parse(candidate.toJSON())!.AsObject();
    }
}
