using System.Net;

namespace Rony.Models
{
    public class Message
    {
        public Message(byte[] body, object sender)
        {
            Body = body;
            Sender = sender;
            RemoteEndPoint = sender as EndPoint;
        }

        public Message(string body, object sender) : this(body.GetBytes(), sender)
        {
        }

        public Message(byte[] body, object sender, EndPoint remoteEndPoint) : this(body, sender)
        {
            RemoteEndPoint = remoteEndPoint;
        }

        public byte[] Body { get; set; }

        /// <summary>
        /// Opaque handle the listener uses to reply to this message.
        /// </summary>
        public object Sender { get; set; }

        /// <summary>
        /// The client's address, when the listener knows it.
        /// </summary>
        public EndPoint RemoteEndPoint { get; set; }

        public string BodyString => Body.GetString();
    }
}
