namespace Trackdub.Contracts.ModelOptimization;

public interface IModelVariantRegistrar
{
    Task RegisterAsync(
        ModelOptimizedVariantRegistration registration,
        CancellationToken cancellationToken = default);
}
