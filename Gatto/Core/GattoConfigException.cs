namespace Gatto.Core;

//the message is what the user sees, so it must read on its own, and a subclass must add facts without changing it
public class GattoConfigException(string message) : Exception(message);
