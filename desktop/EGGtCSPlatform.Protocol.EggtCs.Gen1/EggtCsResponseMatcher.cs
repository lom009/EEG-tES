using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;

namespace EGGtCSPlatform.Protocol.EggtCs.Gen1;

public sealed class EggtCsResponseMatcher : IResponseMatcher
{
    public bool IsMatch(
        CommandDescriptor descriptor,
        WireMessage request,
        DecodedProtocolMessage response
    )
    {
        if (
            response.Kind != ProtocolMessageKind.Response
            || descriptor.ExpectedResponseCommand != response.Command
        )
        {
            return false;
        }

        return descriptor.ResponseCorrelation == ResponseCorrelationMode.CommandOnly
            || response.Index == request.Index;
    }
}
