using Recrovit.AI.Core.Workflows;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby.Workflows;

// RGF-DOC: rgf.core.recroby.architecture
/// <summary>Configures the built-in generic intent workflow.</summary>
public static class RgfRecrobyWorkflowConfigurationExtensions
{
    /// <summary>Adds the generic intent workflow unless rgf.recroby is already registered.</summary>
    /// <param name="configuration">The shared workflow and output-contract registries.</param>
    /// <remarks>The AI decision selects general chat or an application-not-implemented response.
    /// Both terminal branches return strings and do not request user input. An existing workflow is preserved.</remarks>
    public static void AddDefaultRecrobyWorkflow(this RecrovitWorkflowConfiguration configuration)
    {
        try
        {
            configuration.Workflows.Get("rgf.recroby");
            return;
        }
        catch (WorkflowException exception) when (exception.Code == WorkflowErrorCode.UnknownWorkflow) { }
        configuration.Contracts.Register<string>("rgf.recroby.response");
        configuration.Workflows.Register(new WorkflowDefinition
        {
            Id = "rgf.recroby", Name = "Recroby", EntryAgentId = "rgf.recroby.intent",
            Agents = [new AgentDefinition
            {
                Id = "rgf.recroby.intent", Name = "Intent", Type = AgentType.Decision,
                DecisionExecutionMode = DecisionExecutionMode.AI,
                Instruction = """
                    Classify the user's current request.
                    Use "application" when the request concerns the current application,
                    its data, entities, properties, behavior or functionality.
                    Use "chat" for general conversation or requests unrelated to the current application.
                    Use the conversation history to interpret follow-up requests.
                    Only classify the request. Do not answer the user's request.
                    """,
                IncludeConversationHistory = true, AllowInputRequest = false
            }, new AgentDefinition
            {
                Id = "rgf.recroby.chat", Name = "Chat", Type = AgentType.Executor,
                ExecutorExecutionMode = ExecutorExecutionMode.AI,
                Instruction = "Respond naturally to the user's message as a general conversation assistant.", IncludeConversationHistory = true,
                AllowInputRequest = false, OutputContractId = "rgf.recroby.response"
            }, new AgentDefinition
            {
                Id = "rgf.recroby.application", Name = "Application", Type = AgentType.Executor,
                ExecutorExecutionMode = ExecutorExecutionMode.AI,
                Instruction = "Briefly tell the user, in their language, that application-specific request handling is not implemented yet.",
                AllowInputRequest = false, OutputContractId = "rgf.recroby.response"
            }],
            Relations = [new AgentRelation
            {
                SourceAgentId = "rgf.recroby.intent", TargetAgentId = "rgf.recroby.chat", DecisionKey = "chat"
            }, new AgentRelation
            {
                SourceAgentId = "rgf.recroby.intent", TargetAgentId = "rgf.recroby.application", DecisionKey = "application"
            }]
        });
    }
}
