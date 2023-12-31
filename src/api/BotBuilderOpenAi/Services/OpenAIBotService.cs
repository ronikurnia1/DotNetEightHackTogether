﻿using Azure.AI.OpenAI;
using Azure.Search.Documents;
using BotBuilderOpenAi.Extensions;
using BotBuilderOpenAi.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.AI.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.AI.OpenAI.ChatCompletion;
using System.Text.Json;

namespace BotBuilderOpenAi.Services;

public class OpenAIBotService
{
    private readonly SearchClient searchClient;
    private readonly OpenAIConfig config;
    private readonly IChatCompletion chatCompletion;

    public OpenAIBotService(SearchClient searchClient, OpenAIClient openAIClient, OpenAIConfig config)
    {
        this.searchClient = searchClient;
        this.config = config;
        chatCompletion = new AzureOpenAIChatCompletion(config.ChatGptDeployment, openAIClient);
    }

    public async Task<Response> ReplyAsync(
        ChatTurn[] history,
        RequestOverrides? overrides,
        CancellationToken cancellationToken = default)
    {
        var top = overrides?.Top ?? 3;
        var useSemanticCaptions = overrides?.SemanticCaptions ?? false;
        var useSemanticRanker = overrides?.SemanticRanker ?? false;
        var excludeCategory = overrides?.ExcludeCategory ?? null;
        var filter = excludeCategory is null ? null : $"category ne '{excludeCategory}'";

        //var chat = kernel.GetService<IChatCompletion>();
        //var embedding = kernel.GetService<ITextEmbeddingGeneration>();

        float[]? embeddings = null;
        var question = history.LastOrDefault()?.User is { } userQuestion
            ? userQuestion : throw new InvalidOperationException("Use question is null");

        //if (overrides?.RetrievalMode != "Text" && embedding is not null)
        //{
        //    try
        //    {
        //        embeddings = (await embedding.GenerateEmbeddingAsync(question, cancellationToken: cancellationToken)).ToArray();
        //    }
        //    catch (Exception e)
        //    {
        //        Console.WriteLine(e);
        //    }
        //}

        // step 1
        // use llm to get query if retrieval mode is not vector
        string? query = null;
        if (overrides?.RetrievalMode != "Vector")
        {
            var getQueryChat = chatCompletion.CreateNewChat(@"You are a helpful AI assistant, generate search query for follow up question.
Make your respond simple and precise. Return the query only, do not return any other text.
e.g.
Northwind Health Plus AND standard plan.
standard plan AND dental AND employee benefit.
");

            getQueryChat.AddUserMessage(question);
            var result = await chatCompletion.GetChatCompletionsAsync(
                getQueryChat,
                cancellationToken: cancellationToken);

            if (result.Count != 1)
            {
                throw new InvalidOperationException("Failed to get search query");
            }

            query = result[0].ModelResult.GetOpenAIChatResult().Choice.Message.Content;
        }


        // step 2
        // use query to search related docs

        SupportingContentRecord[] documentContentList = [];
        try
        {
            documentContentList = await searchClient.QueryDocumentsAsync(query, null, overrides, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }

        string documentContents = string.Empty;
        if (documentContentList.Length == 0)
        {
            documentContents = "no source available.";
        }
        else
        {
            documentContents = string.Join("\r", documentContentList.Select(x => $"{x.Title}:{x.Content}"));
        }

        // step 3
        // put together related docs and conversation history to generate answer
        var answerChat = chatCompletion.CreateNewChat(
            "You are a system assistant who helps the company employees with their healthcare " +
            "plan questions, and questions about the employee handbook. Be brief in your answers");


        // add chat history
        foreach (var turn in history)
        {
            answerChat.AddUserMessage(turn.User);
            if (turn.Bot is { } botMessage)
            {
                answerChat.AddAssistantMessage(botMessage);
            }
        }

        // format prompt
        answerChat.AddUserMessage(@$" ## Source ##
{documentContents}
## End ##

You answer needs to be a json object with the following format.
{{
    ""answer"": // the answer to the question, add a source reference to the end of each sentence. e.g. Apple is a fruit [reference1.pdf][reference2.pdf]. If no source available, put the answer as I don't know.
    ""thoughts"": // brief thoughts on how you came up with the answer, e.g. what sources you used, what you thought about, etc.
}}");

        // get answer
        var answer = await chatCompletion.GetChatCompletionsAsync(answerChat, cancellationToken: cancellationToken);
        var answerJson = answer?[0].ModelResult.GetOpenAIChatResult().Choice.Message.Content ?? throw new InvalidOperationException("Failed to get answer");
        var answerObject = JsonSerializer.Deserialize<JsonElement>(answerJson);
        var ans = answerObject.GetProperty("answer").GetString() ?? throw new InvalidOperationException("Failed to get answer");
        var thoughts = answerObject.GetProperty("thoughts").GetString() ?? throw new InvalidOperationException("Failed to get thoughts");

        // step 4
        // add follow up questions if requested
        if (overrides?.SuggestFollowUpQuestions is true)
        {
            var followUpQuestionChat = chatCompletion.CreateNewChat(@"You are a helpful AI assistant");
            followUpQuestionChat.AddUserMessage($@"Generate three follow-up question based on the answer you just generated.
# Answer
{ans}

# Format of the response
Return the follow-up question as a json string list.
e.g.
[
    ""What is the deductible?"",
    ""What is the co-pay?"",
    ""What is the out-of-pocket maximum?""
]");

            var followUpQuestions = await chatCompletion.GetChatCompletionsAsync(
                followUpQuestionChat,
                cancellationToken: cancellationToken);

            var followUpQuestionsJson = followUpQuestions[0].ModelResult.GetOpenAIChatResult().Choice.Message.Content;
            var followUpQuestionsObject = JsonSerializer.Deserialize<JsonElement>(followUpQuestionsJson);
            var followUpQuestionsList = followUpQuestionsObject.EnumerateArray().Select(x => x.GetString()).ToList();
            foreach (var followUpQuestion in followUpQuestionsList)
            {
                ans += $" <<{followUpQuestion}>> ";
            }
        }

        return new Response(
            DataPoints: documentContentList,
            Answer: ans,
            Thoughts: thoughts,
            CitationBaseUrl: config.ToCitationBaseUrl());

    }
}
