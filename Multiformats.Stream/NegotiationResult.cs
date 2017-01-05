namespace Multiformats.Stream
{
    public class NegotiationResult
    {
        public string Protocol { get; }
        public IMultistreamHandler Handler { get; }

        public NegotiationResult(string protocol = null, IMultistreamHandler handler = null)
        {
            Protocol = protocol;
            Handler = handler;
        }
    }
}