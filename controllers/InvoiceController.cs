using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

[ApiController]
[Route("api/analyze-invoice")]
public class InvoiceController : ControllerBase
{
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly ILogger<InvoiceController> _logger;

    public InvoiceController(IConfiguration configuration, ILogger<InvoiceController> logger)
    {
        _endpoint = configuration["FormRecognizerEndpoint"];
        _apiKey = configuration["FormRecognizerApiKey"];
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> AnalyzeInvoice([FromBody] InvoiceRequest request)
    {
        _logger.LogInformation("Received API request from SyncedBackend with OCR Text: {OcrText}, File URL: {FileUrl}", request.OcrText, request.FileUrl);

        if (string.IsNullOrEmpty(request.OcrText) && string.IsNullOrEmpty(request.FileUrl))
        {
            _logger.LogWarning("Invalid request received. Both OCR text and File URL are empty.");
            return BadRequest(new { error = "Either OCR text or File URL must be provided." });
        }

        try
        {
            object invoiceData;

            if (!string.IsNullOrEmpty(request.FileUrl))
            {
                _logger.LogInformation("Downloading invoice from URL: {FileUrl}", request.FileUrl);
                using var httpClient = new HttpClient();
                var fileBytes = await httpClient.GetByteArrayAsync(request.FileUrl);
                using var memoryStream = new MemoryStream(fileBytes);
                invoiceData = await ProcessInvoice(memoryStream);
            }
            else
            {
                _logger.LogInformation("Processing invoice using provided OCR text.");
                invoiceData = await ProcessInvoiceText(request.OcrText);
            }

            _logger.LogInformation("Successfully processed invoice, sending response to SyncedBackend.");
            return Ok(invoiceData);
        }
        catch (Exception ex)
        {
            _logger.LogError("Error processing invoice: {ErrorMessage}", ex.Message);
            return StatusCode(500, new { error = ex.Message });
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

        var extractedFields = analyzedDocument.Fields.ToDictionary(
            field => field.Key,
            field => field.Value.Content ?? "Unknown"
        );

        return new
        {
            Vendor = extractedFields.GetValueOrDefault("VendorName", "Unknown"),
            InvoiceNumber = extractedFields.GetValueOrDefault("InvoiceId", "Unknown"),
            TotalAmount = extractedFields.GetValueOrDefault("InvoiceTotal", "Unknown"),
            InvoiceDate = extractedFields.GetValueOrDefault("InvoiceDate", "Unknown"),
            DueDate = extractedFields.GetValueOrDefault("DueDate", "Unknown"),
            CustomerName = extractedFields.GetValueOrDefault("CustomerName", "Unknown"),
            Items = extractedFields.GetValueOrDefault("Items", "Unknown"),
            Currency = extractedFields.GetValueOrDefault("Currency", "Unknown"),
            AdditionalFields = extractedFields
        };
    }

    private async Task<object> ProcessInvoiceText(string ocrText)
    {
        // Example: Call OpenAI or process text further if needed
        return new
        {
            ProcessedText = ocrText
        };
    }
}

// ✅ Define the InvoiceRequest class at the bottom of the file
public class InvoiceRequest
{
    public string OcrText { get; set; }  // OCR-extracted text from SyncedBackend
    public string FileUrl { get; set; }  // URL of the uploaded invoice document (optional)
}
