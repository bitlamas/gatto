namespace Gatto.Core.Client;

//unsealed, GattoContextOverflowException subtypes it so every existing catch site keeps seeing a connection failure
public class GattoConnectionException(string message) : Exception(message);
