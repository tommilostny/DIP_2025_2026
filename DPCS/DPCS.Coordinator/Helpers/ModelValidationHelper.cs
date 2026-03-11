using System.ComponentModel.DataAnnotations;

namespace DPCS.Coordinator.Helpers;

/// <summary>
/// Helper class containing an endpoint filter for validating incoming API request models using DataAnnotations.
/// These models include MaskJobSpecsModel and DictionaryJobSpecsModel, which are used for job submissions via the API.
/// </summary>
public static class ModelValidationHelper
{
    public static async ValueTask<object?> ValidationFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // Find the model to validate from the endpoint arguments
        var modelToValidate = context.Arguments.FirstOrDefault(arg => arg is MaskJobSpecsModel or DictionaryJobSpecsModel);

        if (modelToValidate is null)
        {
            // If no model is found that we're supposed to validate, just continue.
            // This also handles cases where the model is null, which ASP.NET Core
            // might have already turned into a 400 Bad Request.
            return await next(context);
        }

        var validationContext = new ValidationContext(modelToValidate);
        var validationResults = new List<ValidationResult>();

        // Try to validate the model object based on its DataAnnotations
        if (!Validator.TryValidateObject(modelToValidate, validationContext, validationResults, validateAllProperties: true))
        {
            // If validation fails, group the errors by member name and return a ValidationProblem response.
            var errors = validationResults
                .SelectMany(result => result.MemberNames.DefaultIfEmpty(""), (result, memberName) => new { memberName, result.ErrorMessage })
                .GroupBy(error => error.memberName)
                .ToDictionary(group => group.Key, group => group.Select(g => g.ErrorMessage!).ToArray());

            return Results.ValidationProblem(errors);
        }

        // If validation succeeds, proceed to the endpoint handler.
        return await next(context);
    }
}