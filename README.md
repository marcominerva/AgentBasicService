# Agent Basic Service

Examples to demonstrate configuration and usage of basic agents and workflows from Microsoft Agent Framework, supporting thread persistence and structured output.

## Configuration of the Web API

To configure Azure OpenAI, update the following properties in the `appsettings.json` file:

```json
{
  "AzureOpenAI": {
    "Endpoint": "https://your-resource-name.openai.azure.com/openai/v1/",
    "Deployment": "your-deployment-name",
    "ApiKey": "your-api-key"
  }
}
```

- **Endpoint**: The Azure OpenAI resource endpoint URL
- **Deployment**: The name of your deployed model
- **ApiKey**: Your Azure OpenAI API key
