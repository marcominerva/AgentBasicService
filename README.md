# Agent Basic Service

An example to demonstrate configuration and usage of a basic agent from Microsoft Agent Framework in an ASP.NET Core Minimal API application, supporting thread persistence and structured output with Azure OpenAI.

## Configuration

To configure Azure OpenAI, update the following properties in the `appsettings.json` file:

```json
{
  "AzureOpenAI": {
    "Endpoint": "https://your-resource-name.openai.azure.com/",
    "Deployment": "your-deployment-name",
    "ApiKey": "your-api-key"
  }
}
```

- **Endpoint**: The Azure OpenAI resource endpoint URL
- **Deployment**: The name of your deployed model
- **ApiKey**: Your Azure OpenAI API key
