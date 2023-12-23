﻿using GraphQL;
using GraphQL.Client.Http;
using GraphQL.Client.Serializer.Newtonsoft;
using Microsoft.Extensions.Logging;
using MondaySharp.NET.Application.Common;
using MondaySharp.NET.Application.Entities;
using MondaySharp.NET.Application.Interfaces;
using MondaySharp.NET.Domain.ColumnTypes;
using MondaySharp.NET.Domain.Common;
using MondaySharp.NET.Infrastructure.Utilities;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MondaySharp.NET.Infrastructure.Persistence;

public partial class MondayClient : IMondayClient, IDisposable
{
    /// <summary>
    /// 
    /// </summary>
    private readonly MondayOptions? _mondayOptions;

    /// <summary>
    /// 
    /// </summary>
    private readonly GraphQLHttpClient? _graphQLHttpClient;
    
    /// <summary>
    /// 
    /// </summary>
    private bool disposedValue;

    /// <summary>
    /// 
    /// </summary>
    private readonly ILogger<MondayClient>? _logger;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="options"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public MondayClient(ILogger<MondayClient> logger, Action<MondayOptions> options)
    {
        // Create New Monday Options.
        _mondayOptions = new MondayOptions();

        // Invoke Delegate That Will Assign The Options To The Monday Options.
        options.Invoke(_mondayOptions);

        // Build The GraphQL Client.
        _graphQLHttpClient = new GraphQLHttpClient(new GraphQLHttpClientOptions()
        {
            EndPoint = _mondayOptions.EndPoint,
        },
        new NewtonsoftJsonSerializer());

        // Create Header For The GraphQL Client.
        _graphQLHttpClient.HttpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _mondayOptions.Token);

        // Set The API Version.
        _graphQLHttpClient.HttpClient.DefaultRequestHeaders.Add("API-Version", "2023-10");

        // Set The Logger.
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // Stop The Client From Being Created Without Options.
    public MondayClient() { }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="disposing"></param>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                _logger?.LogInformation("Disposing Monday Client.");
                _graphQLHttpClient?.HttpClient?.Dispose();

                _logger?.LogInformation("Disposing GraphQL Client.");
                _graphQLHttpClient?.Dispose();
            }

            disposedValue = true;
        }
    }

    /// <summary>
    /// 
    /// </summary>
    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="boardId"></param>
    /// <param name="columnValues"></param>
    /// <param name="limit"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public async IAsyncEnumerable<Application.MondayResponse<T?>> GetBoardItemsAsync<T>(ulong boardId, ColumnValue[] columnValues, int limit = 25,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : MondayRow, new()
    {
        // If The GraphQL Client Is Null, Return Null.
        if (this._graphQLHttpClient == null) yield break;

        // Create New Instance Of T Type.
        T instance = Activator.CreateInstance<T>();

        // Check for multiple properties of the same type
        foreach (KeyValuePair<Type, string> unSupportedType in MondayUtilties.UnsupportedTypes)
        {
            int count = instance.GetType().GetProperties().Count(propertyInfo => propertyInfo.PropertyType == unSupportedType.Key);
            if (count > 1) throw new NotImplementedException(unSupportedType.Value);
        }

        // Create New
        StringBuilder stringBuilder = new();

        // Get each property in instance.
        foreach (PropertyInfo propertyInfo in instance.GetType().GetProperties())
        {
            // Attempt to get the type from the GetItemsQueryBuilder
            if (MondayUtilties.GetItemsQueryBuilder.TryGetValue(propertyInfo.PropertyType, out string? query))
            {
                // Append the query to the string builder.
                stringBuilder.Append(query);
            }
        }

        // Construct the GraphQL query
        GraphQLRequest keyValuePairs = new()
        {
            Query = $@"query($limit: Int, $boardId: ID!, $columnValues: [ItemsPageByColumnValuesQuery!]) {{
              items_page_by_column_values(limit: $limit, board_id: $boardId, columns: $columnValues) {{
                cursor
                items {{
                  id
                  name
                  state
                  column_values {{
                    id
                    text
                    type
                    value
                  }}
                    {stringBuilder}
                }}
              }}
            }}",
            Variables = new
            {
                boardId,
                columnValues = columnValues.Select(query => new
                {
                    column_id = query.Id,
                    column_values = new List<string?> { query.Text }
                }),
                limit
            }
        };

        // Execute The Query.
        GraphQLResponse<GetBoardItemsByColumnValuesResponse> graphQLResponse = 
            await this._graphQLHttpClient.SendQueryAsync<GetBoardItemsByColumnValuesResponse>(keyValuePairs, cancellationToken);

        // If The Response Is Not Null, And The Data Is Not Null, And The Errors Is Null, Return The Data.
        if (graphQLResponse.Errors is null && graphQLResponse.Data?.ItemsPageByColumnValue?.Items?.Count > 0)
        {
            // Get The Column Property Map.
            Dictionary<string, string> columnPropertyMap = MondayUtilties.GetColumnPropertyMap<T>();

            foreach (Item item in graphQLResponse.Data.ItemsPageByColumnValue.Items)
            {

                // Check if we need to break out of the loop
                if (cancellationToken.IsCancellationRequested) yield break;

                // Attempt To Bind The Items.
                if (MondayUtilties.TryBindColumnDataAsync(columnPropertyMap!, item!, ref instance))
                {               
                    yield return new Application.MondayResponse<T?>()
                    {
                        IsSuccessful = true,
                        Data = instance
                    };
                }
            }
        }
        else if (graphQLResponse.Errors is not null)
        {
            yield return new Application.MondayResponse<T?>()
            {
                IsSuccessful = false,
                Errors = graphQLResponse.Errors?.Select(x => x.Message).ToHashSet()
            };
        }

        yield break;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="boardId"></param>
    /// <param name="limit"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public async IAsyncEnumerable<Application.MondayResponse<T?>> GetBoardItemsAsync<T>(ulong boardId, int limit = 25, 
        [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : MondayRow, new()
    {
        // If The GraphQL Client Is Null, Return Null.
        if (this._graphQLHttpClient == null) yield break;

        // Create New Instance Of T Type.
        T instance = Activator.CreateInstance<T>();

        // Check for multiple properties of the same type
        foreach (KeyValuePair<Type, string> unSupportedType in MondayUtilties.UnsupportedTypes)
        {
            int count = instance.GetType().GetProperties().Count(propertyInfo => propertyInfo.PropertyType == unSupportedType.Key);
            if (count > 1) throw new NotImplementedException(unSupportedType.Value);
        }

        // Create New
        StringBuilder stringBuilder = new();

        // Get each property in instance.
        foreach (PropertyInfo propertyInfo in instance.GetType().GetProperties())
        {
            // Attempt to get the type from the GetItemsQueryBuilder
            if (MondayUtilties.GetItemsQueryBuilder.TryGetValue(propertyInfo.PropertyType, out string? query))
            {
                // Append the query to the string builder.
                stringBuilder.Append(query);
            }
        }

        // Construct the GraphQL query
        GraphQLRequest keyValuePairs = new()
        {
            Query = $@"query($limit: Int, $boardId: ID!) {{
                boards (ids: [$boardId]) {{
                    id
                    name
                    state
                    board_kind
                    board_folder_id
                    description
                    workspace_id
                    item_terminology
                    items_count
                    permissions
                    items_page (limit: $limit) {{
                        cursor
                        items {{
                            id
                            name
                            state
                            column_values {{
                                id
                                text
                                type
                                value
                            }}
                            {stringBuilder}
                        }}
                    }}
                }}
            }}",
            Variables = new
            {
                boardId,
                limit
            }
        };

        // Execute The Query.
        GraphQLResponse<GetBoardItemsResponse> graphQLResponse = await this._graphQLHttpClient.SendQueryAsync<GetBoardItemsResponse>(keyValuePairs, cancellationToken);

        // If The Response Is Not Null, And The Data Is Not Null, And The Errors Is Null, Return The Data.
        if (graphQLResponse.Errors is null
            && graphQLResponse.Data?.Boards?.Count > 0
            && graphQLResponse.Data?.Boards?.Any(x => x.ItemsCount > 0) == true)
        {
            // Get The Column Property Map.
            Dictionary<string, string> columnPropertyMap = MondayUtilties.GetColumnPropertyMap<T>();

            foreach (Item item in graphQLResponse.Data.Boards.Select(x => x.ItemsPage!)
                .Where(x => x.Items?.Count > 0)
                .SelectMany(x => x.Items))
            {
                // Check if we need to break out of the loop
                if (cancellationToken.IsCancellationRequested) yield break;

                // Attempt To Bind The Items.
                if (MondayUtilties.TryBindColumnDataAsync(columnPropertyMap!, item!, ref instance))
                {
                    yield return new Application.MondayResponse<T?>()
                    {
                        IsSuccessful = true,
                        Data = instance
                    };
                }
            }
        }
        else if (graphQLResponse.Errors is not null)
        {
            yield return new Application.MondayResponse<T?>()
            {
                IsSuccessful = false,
                Errors = graphQLResponse.Errors?.Select(x => x.Message).ToHashSet()
            };
        }

        yield break;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="boardId"></param>
    /// <param name="items"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<Application.MondayResponse<Dictionary<string, Item>?>> CreateBoardItemsAsync(ulong boardId, Item[] items, 
        CancellationToken cancellationToken = default)
    {
        // If The GraphQL Client Is Null, Return Null.
        if (this._graphQLHttpClient == null) return new Application.MondayResponse<Dictionary<string, Item>?>()
        {
            IsSuccessful = false,
            Errors = ["GraphQL Client Is Null."]
        };

        // Create The Response Parameters.
        const string RESPONSE_PARAMS = @$"{{id name}}";

        // Create parameters for the query
        StringBuilder parameters = new();

        // Create the mutation
        StringBuilder mutation = new();

        // Append the parameters
        parameters.Append("$boardId: ID!,");

        // Create a dictionary to store variables dynamically
        Dictionary<string, object> variables = new()
        {
            { "boardId", boardId }
        };

        foreach (var item in items.Select((value, i) => new { i, value }))
        {
            // Check if there is an item name.
            if (string.IsNullOrEmpty(item.value.Name))
            {
                throw new NullReferenceException(nameof(item.value.Name));
            }

            // Generate a unique variable name based on the item index
            string variableName = $"columnValues{item.i}";

            // Check if the column values are null
            if (item.value.ColumnValues is not null && item.value.ColumnValues is { Count: > 0})
            {
                // Add the variable to the dictionary
                variables.Add(variableName, MondayUtilties.ToColumnValuesJson(item.value.ColumnValues.Select(x => x.ColumnBaseType).ToList()!));

                // Append the parameters
                parameters.Append($"${variableName}: JSON,");

                // Append the mutation
                mutation.Append($"create_item_{item.i}: create_item(board_id: $boardId, item_name: \"{item.value.Name}\", column_values: ${variableName}) {RESPONSE_PARAMS}");
            }
            else
            {
                // Append the mutation
                mutation.Append($"create_item_{item.i}: create_item(board_id: $boardId, item_name: \"{item.value.Name}\") {RESPONSE_PARAMS}");
            }
        }

        // Construct the GraphQL query
        GraphQLRequest keyValuePairs = new()
        {
            Query = $@"mutation ({parameters}) {{
                {mutation}
            }}",
            Variables = variables
        };

        // Execute The Query.
        GraphQLResponse<Dictionary<string, Item>> graphQLResponse = 
            await this._graphQLHttpClient.SendMutationAsync<Dictionary<string, Item>>(keyValuePairs, cancellationToken);

        // If The Response Is Not Null, And The Data Is Not Null, And The Errors Is Null, Return The Data.
        if (graphQLResponse.Errors is null && graphQLResponse.Data != null)
        {
            return new Application.MondayResponse<Dictionary<string, Item>?>()
            {
                IsSuccessful = true,
                Data = graphQLResponse.Data
            };
        }

        return new Application.MondayResponse<Dictionary<string, Item>?>()
        {
            IsSuccessful = false,
            Errors = graphQLResponse.Errors?.Select(x => x.Message).ToHashSet()
        };
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="updates"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="NullReferenceException"></exception>
    public async Task<Application.MondayResponse<Dictionary<string, Update>?>> CreateItemsUpdateAsync(
        Update[] updates,
        CancellationToken cancellationToken = default)
    {
        // If The GraphQL Client Is Null, Return Null.
        if (this._graphQLHttpClient == null) return new Application.MondayResponse<Dictionary<string, Update>?>()
        {
            IsSuccessful = false,
            Errors = ["GraphQL Client Is Null."]
        };

        // Create The Response Parameters.
        const string RESPONSE_PARAMS = @$"{{id text_body}}";

        // Create parameters for the query
        StringBuilder parameters = new();

        // Create the mutation
        StringBuilder mutation = new();

        // Create a dictionary to store variables dynamically
        Dictionary<string, object> variables = [];

        // Append the parameters
        foreach (var update in updates.Select((value, i) => new { i, value }))
        {
            // Check if there is an item name.
            if (update.value.Id.GetValueOrDefault() == 0)
            {
                throw new NullReferenceException(nameof(update.value.Id));
            }

            // Generate a unique variable name based on the item index
            string variableName = $"body{update.i}";

            // Check if the column values are null
            if (update.value.TextBody is not null)
            {
                // Add the variable to the dictionary
                variables.Add(variableName, update.value.TextBody);

                // Append the parameters
                parameters.Append($"${variableName}: String!,");

                // Append the mutation
                mutation.Append($"create_update_{update.i}: create_update(item_id: {update.value.Id}, body: ${variableName}) {RESPONSE_PARAMS}");
            }
        }

        // Construct the GraphQL query
        GraphQLRequest keyValuePairs = new()
        {
            Query = $@"mutation ({parameters}) {{
                {mutation}
            }}",
            Variables = variables
        };

        // Execute The Query.
        GraphQLResponse<Dictionary<string, Update>> graphQLResponse =
            await this._graphQLHttpClient.SendMutationAsync<Dictionary<string, Update>>(keyValuePairs, cancellationToken);

        // If The Response Is Not Null, And The Data Is Not Null, And The Errors Is Null, Return The Data.
        if (graphQLResponse.Errors is null && graphQLResponse.Data != null)
        {
            return new Application.MondayResponse<Dictionary<string, Update>?>()
            {
                IsSuccessful = true,
                Data = graphQLResponse.Data
            };
        }

        // Return Null.
        return new Application.MondayResponse<Dictionary<string, Update>?>()
        {
            IsSuccessful = false,
            Errors = graphQLResponse.Errors?.Select(x => x.Message).ToHashSet()
        };
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="items"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="NullReferenceException"></exception>
    public async Task<Application.MondayResponse<Dictionary<string, Item>?>> DeleteItemsAsync(
        Item[] items,
        CancellationToken cancellationToken = default)
    {
        // If The GraphQL Client Is Null, Return Null.
        if (this._graphQLHttpClient == null) return new Application.MondayResponse<Dictionary<string, Item>?>()
        {
            IsSuccessful = false,
            Errors = ["GraphQL Client Is Null."]
        };

        // Create The Response Parameters.
        const string RESPONSE_PARAMS = @$"{{id}}";

        // Create parameters for the query
        StringBuilder parameters = new();

        // Create the mutation
        StringBuilder mutation = new();

        // Create a dictionary to store variables dynamically
        Dictionary<string, object> variables = [];

        // Append the parameters
        foreach (var item in items.Select((value, i) => new { i, value }))
        {
            // Check if there is an item name.
            if (item.value.Id == 0)
            {
                throw new NullReferenceException(nameof(item.value.Id));
            }

            // Generate a unique variable name based on the item index
            string variableName = $"itemId{item.i}";

            // Add the variable to the dictionary
            variables.Add(variableName, item.value.Id);

            // Append the parameters
            parameters.Append($"${variableName}: ID!,");

            // Append the mutation
            mutation.Append($"delete_item_{item.i}: delete_item(item_id: ${variableName}) {RESPONSE_PARAMS}");
        }

        // Construct the GraphQL query
        GraphQLRequest keyValuePairs = new()
        {
            Query = $@"mutation ({parameters}) {{
                {mutation}
            }}",

            Variables = variables
        };

        // Execute The Query.
        GraphQLResponse<Dictionary<string, Item>> graphQLResponse =
            await this._graphQLHttpClient.SendMutationAsync<Dictionary<string, Item>>(keyValuePairs, cancellationToken);

        // If The Response Is Not Null, And The Data Is Not Null, And The Errors Is Null, Return The Data.
        if (graphQLResponse.Errors is null && graphQLResponse.Data != null)
        {
            return new Application.MondayResponse<Dictionary<string, Item>?>()
            {
                IsSuccessful = true,
                Data = graphQLResponse.Data
            };
        }
        else if (graphQLResponse.Errors is not null)
        {
            return new Application.MondayResponse<Dictionary<string, Item>?>()
            {
                IsSuccessful = false,
                Errors = graphQLResponse.Errors?.Select(x => x.Message).ToHashSet()
            };
        }

        // Return Null.
        return new Application.MondayResponse<Dictionary<string, Item>?>()
        {
            IsSuccessful = false,
            Errors = []
        };
    }
}