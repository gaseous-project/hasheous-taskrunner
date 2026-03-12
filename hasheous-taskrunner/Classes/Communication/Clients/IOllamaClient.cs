using System.Collections.Generic;
using System.Threading.Tasks;

namespace hasheous_taskrunner.Classes.Communication.Clients
{
    /// <summary>
    /// Interface for Ollama service client.
    /// Extends IExternalServiceClient with Ollama-specific operations.
    /// </summary>
    public interface IOllamaClient : IExternalServiceClient
    {
        // Inherits GetAsync, PostAsync from IExternalServiceClient
        // Can be extended with Ollama-specific methods if needed in the future
    }
}
