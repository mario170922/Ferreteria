using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Ferreteria.Services;

// Los campos numéricos del navegador (type="number") siempre envían el punto como decimal: "1428.57".
// El servidor corre en español de Colombia, donde el punto es separador de miles, y sin esto
// "1428.57" se leería como 142857. Aquí se lee primero con punto decimal y, si no se puede,
// con la forma colombiana ("1428,57").
public class DecimalModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext contexto)
    {
        var valor = contexto.ValueProvider.GetValue(contexto.ModelName);
        if (valor == ValueProviderResult.None) return Task.CompletedTask;
        contexto.ModelState.SetModelValue(contexto.ModelName, valor);

        var texto = valor.FirstValue?.Trim();
        if (string.IsNullOrEmpty(texto))
        {
            // Vacío: null si el campo lo permite; si no, se deja el valor por defecto
            if (Nullable.GetUnderlyingType(contexto.ModelType) is not null)
                contexto.Result = ModelBindingResult.Success(null);
            return Task.CompletedTask;
        }

        if (decimal.TryParse(texto, NumberStyles.Number, CultureInfo.InvariantCulture, out var numero) ||
            decimal.TryParse(texto, NumberStyles.Number, new CultureInfo("es-CO"), out numero))
        {
            contexto.Result = ModelBindingResult.Success(numero);
        }
        else
        {
            contexto.ModelState.TryAddModelError(contexto.ModelName, "Escribe un número válido.");
        }
        return Task.CompletedTask;
    }
}

public class DecimalModelBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext contexto) =>
        contexto.Metadata.ModelType == typeof(decimal) || contexto.Metadata.ModelType == typeof(decimal?)
            ? new DecimalModelBinder()
            : null;
}
