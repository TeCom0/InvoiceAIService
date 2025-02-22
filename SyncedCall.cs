using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Net;
using System.Text.Json;
using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;

public class Function1
{
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly IConfiguration _configuration;

    public Function1(IConfiguration configuration)
    {
        _configuration = configuration;
        _endpoint = _configuration["FormRecognizerEndpoint"];
        _apiKey = _configuration["FormRecognizerApiKey"];
    }

    [Function("AnalyzeInvoice")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
    {
        try
        {
            if (!req.Headers.TryGetValues("Content-Type", out var contentTypeValues) ||
                !contentTypeValues.First().Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                return await CreateErrorResponse(req, "Multipart content expected.");
            }

            using var memoryStream = new MemoryStream();
            await req.Body.CopyToAsync(memoryStream);
            memoryStream.Position = 0;

            var result = await ProcessInvoice(memoryStream);
            return await CreateResponse(req, result);
        }
        catch (Exception ex)
        {
            return await CreateErrorResponse(req, $"Error processing invoice: {ex.Message}");
        }
    }

   private async Task<object> ProcessInvoice(Stream fileStream)
{
    var credential = new AzureKeyCredential(_apiKey);
    var client = new DocumentAnalysisClient(new Uri(_endpoint), credential);

    var operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-invoice", fileStream);
    var result = operation.Value;

    if (result.Documents.Count == 0)
    {
        throw new InvalidOperationException("No documents found in the analysis result.");
    }

    var analyzedDocument = result.Documents[0];

    // Extract fields dynamically
    var extractedFields = analyzedDocument.Fields.ToDictionary(
        field => field.Key,
        field => field.Value.Content ?? "Unknown"
    );

    // ✅ Extracting Line Items Properly
    var items = new List<object>();
    if (analyzedDocument.Fields.TryGetValue("Items", out var itemsField) && itemsField.FieldType == DocumentFieldType.List)
    {
        var itemList = itemsField.Value as IReadOnlyList<DocumentField>;
        if (itemList != null)
        {
            foreach (var item in itemList)
            {
                var itemDictionary = item.Value as IReadOnlyDictionary<string, DocumentField>;
                if (itemDictionary != null)
                {
                    items.Add(new
                    {
                        Description = itemDictionary.TryGetValue("Description", out var descField) ? descField.Content ?? "Unknown" : "Unknown",
                        Quantity = itemDictionary.TryGetValue("Quantity", out var qtyField) ? qtyField.Content ?? "Unknown" : "Unknown",
                        Price = itemDictionary.TryGetValue("Price", out var priceField) ? priceField.Content ?? "Unknown" : "Unknown",
                        SubTotal = itemDictionary.TryGetValue("Subtotal", out var subtotalField) ? subtotalField.Content ?? "Unknown" : "Unknown"
                    });
                }
            }
        }
    }

    var standardizedInvoice = new
    {
        Vendor = extractedFields.GetValueOrDefault("VendorName", "Unknown"),
        InvoiceNumber = extractedFields.GetValueOrDefault("InvoiceId", "Unknown"),
        TotalAmount = extractedFields.GetValueOrDefault("InvoiceTotal", "Unknown"),
        InvoiceDate = extractedFields.GetValueOrDefault("InvoiceDate", "Unknown"),
        DueDate = extractedFields.GetValueOrDefault("DueDate", "Unknown"),
        CustomerName = extractedFields.GetValueOrDefault("CustomerName", "Unknown"),
        CustomerAddress = extractedFields.GetValueOrDefault("CustomerAddress", "Unknown"),
        Items = items,  // Now correctly extracting item details!
        PaymentMethod = "Bank Transfer (Extract manually if available)", // Placeholder for extraction logic
        Currency = extractedFields.GetValueOrDefault("Currency", "USD"),
        SubTotal = extractedFields.GetValueOrDefault("SubTotal", "Unknown"),
        Tax = extractedFields.GetValueOrDefault("TotalTax", "Unknown"),
        AdditionalFields = extractedFields // Include all other extracted fields
    };

    return standardizedInvoice;
}


    private static string GetFieldContent(AnalyzedDocument analyzedDocument, string fieldName)
    {
        return analyzedDocument.Fields.TryGetValue(fieldName, out var field) ? field.Content ?? "Unknown" : "Unknown";
    }

    private static async Task<HttpResponseData> CreateResponse(HttpRequestData req, object result)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(result);
        return response;
    }

    private static async Task<HttpResponseData> CreateErrorResponse(HttpRequestData req, string errorMessage)
    {
        var response = req.CreateResponse(HttpStatusCode.BadRequest);
        await response.WriteAsJsonAsync(new { error = errorMessage });
        return response;
    }
}