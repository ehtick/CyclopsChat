using System.Xml;

namespace Cyclops.Xmpp;

public interface IXmppClient
{
    event EventHandler Connect;
    event EventHandler<string> ReadRawMessage;
    event EventHandler<string> WriteRawMessage;
    event EventHandler<Exception> Error;

    void SendElement(XmlElement element);
}
