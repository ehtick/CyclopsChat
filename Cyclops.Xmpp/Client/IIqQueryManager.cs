using Cyclops.Xmpp.Protocol.IqQueries;

namespace Cyclops.Xmpp.Client;

public interface IIqQueryManager
{
    event EventHandler<ITimeIq> TimeQueried;
    event EventHandler<ILastIq> LastQueried;
    event EventHandler<IVersionIq> VersionQueried;
}
