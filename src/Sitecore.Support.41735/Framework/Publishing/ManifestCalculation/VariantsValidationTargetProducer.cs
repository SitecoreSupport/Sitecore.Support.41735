using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using Sitecore.Framework.Conditions;
using Sitecore.Framework.Publishing.Item;
using Sitecore.Framework.Publishing.ManifestCalculation;

namespace Sitecore.Support.Framework.Publishing.ManifestCalculation
{
  public class VariantsValidationTargetProducer : Sitecore.Framework.Publishing.ManifestCalculation.VariantsValidationTargetProducer
  {
    public VariantsValidationTargetProducer(IObservable<CandidateValidationContext> publishStream, Sitecore.Framework.Publishing.ManifestCalculation.ITargetItemIndexService targetIndexService, Sitecore.Framework.Publishing.Item.Language[] languages, Guid targetId, int bufferSize, bool republishAllVariants, DateTime publishTimestamp, CancellationTokenSource errorSource, Microsoft.Extensions.Logging.ILogger logger, Microsoft.Extensions.Logging.ILogger diagnosticLogger) : base(publishStream, targetIndexService, languages, targetId, bufferSize, republishAllVariants, publishTimestamp, errorSource, logger, diagnosticLogger)
    {
    }
    protected override void Initialize()
    {
      _publishStream
          .ObserveOn(Scheduler.Default)
          .Buffer(_bufferSize)
          .Subscribe(ctxs =>
          {
            try
            {
              _logger.LogTrace("Loading steps ..");

                    // immediately emit known invalid candidates
                    Emit(ctxs
                        .Where(ctx => !ctx.IsValid)
                        .Select(ctx => new InvalidCandidateTargetContext(_targetId, ctx.Id))
                        .ToArray());

              var candidates = ctxs
                        .Where(ctx => ctx.IsValid)
                        .Select(ctx => ctx.AsValid().Candidate)
                        .Distinct(new SupportComparer())
                        .ToArray();

              if (!candidates.Any()) return;

                    // get all published variants for all candidates in the batch
                    var currentPublishedItemsResult = _targetIndexService.GetItemMetadatas(candidates.Select(c => c.Id).ToArray()).Result;
              var candidatesTargetIndex = currentPublishedItemsResult.ToLookup(r => r.Id);

              foreach (var candidate in candidates)
              {
                _errorToken.ThrowIfCancellationRequested();

                      // get published variants for the current cadidate
                      var publishedCandidateItem = candidatesTargetIndex[candidate.Id].FirstOrDefault() ??
                          new ItemDoesntExistMetadata(candidate.Id);

                var candidateContext = new CandidatePromotionContext(candidate, publishedCandidateItem);

                ProcessCandidate(candidateContext);
              }
            }
            catch (OperationCanceledException)
            {
              if (_errorToken.IsCancellationRequested)
              {
                _logger.LogTrace("VariantsOperationsProducer cancelled.");
                Completed();
              }
            }
            catch (Exception ex)
            {
              _logger.LogError(0, ex, $"Error in the {nameof(VariantsValidationTargetProducer)}");

              Errored(ex);
            }
          },
          ex =>
          {
            Errored(ex);
          },
          () =>
          {
            _logger.LogTrace($"{this.GetType().Name} completed.");
            Completed();
          },
          _errorToken);
    }

    protected override void Emit(CandidatePromotionContext context)
    {
      if (!context.AnyVariantsExist())
      {
        _diagnosticLogger.LogTrace($"[DIAGNOSTICS] {this.GetType()} - Emit Invalid - Id: {context.Candidate.Id} / Target Id: {_targetId}");

        Emit(new InvalidCandidateTargetContext(_targetId, context.Candidate.Id));
      }
      else
      {
        var publishActions = context.CalculatePublishActions(_republish);

        var unmodifiedVariants = publishActions
            .Where(a => a.Action == VariantActionType.Unmodified)
            .Select(a => a.Identity)
            .ToList();

        unmodifiedVariants.ForEach(x =>
            _diagnosticLogger.LogTrace("[DIAGNOSTICS] Skipping (Not Modified) - {id} / Ver: {version} / Lang: {language}", x.Id, x.Version, x.Language));

        var toDelete = publishActions
            .Where(a => a.Action == VariantActionType.Delete)
            .Select(a => a.Identity)
            .ToArray();

        foreach (var x in toDelete)
        {
          _diagnosticLogger.LogTrace("[DIAGNOSTICS] Deleteing - {id} / Ver: {version} / Lang: {language}", x.Id, x.Version, x.Language);
        }

        var toPromote = publishActions
            .Where(a => a.Action == VariantActionType.Create || a.Action == VariantActionType.Replace)
            .Select(a => a.Identity)
            .ToList();

        toPromote.ForEach(x =>
            _diagnosticLogger.LogTrace("[DIAGNOSTICS] Create/Replacing - {id} / Ver: {version} / Lang: {language}", x.Id, x.Version, x.Language));

        _diagnosticLogger.LogTrace($"[DIAGNOSTICS] {this.GetType()} - Emit Valid - Id: {context.Candidate.Id} / Target Id: {_targetId}");

        // If we're not promoting any variants for this item, but there will be some variants remaining on the
        // target, then we should ensure all 'base' item properties are promoted if required...
        if (!toPromote.Any())
        {
          // see if the variants that exist on the target, were modified on master before the base properties
          // were modified on master, if so, promote the latest base Item (ItemProperties and Invariant Fields)

          // Find target variants that are newer or the same as the base item on the target
          var targetVariances = context.TargetMetadata.GetAllVariances()
              .Where(v => v.VariantLastModified >= context.TargetMetadata.BaseLastModified)
              .ToArray();
          
          if (targetVariances.Any())
          {
            // 41295 check that at least one other property except for Updated differeds in source and target 
            var targetProperties = context.TargetMetadata.Properties;
            var sourceProperties = context.Candidate.Node.Properties;

            if (targetVariances.Max(v => v.VariantLastModified) < context.Candidate.Node.BaseLastModified
            && (targetProperties.Name != sourceProperties.Name
            || targetProperties.ParentId != sourceProperties.ParentId
            || targetProperties.TemplateId != sourceProperties.TemplateId
            || targetProperties.MasterId != sourceProperties.MasterId))
            {
              // We are definitely sure the base is NOT up to date on the target
              // promote-base
              _logger.LogWarning("Item base information definitely needs to be promoted : {ItemId} ({ItemName})", context.Candidate.Node.Id, context.Candidate.Node.Properties.Name);

              foreach (var variance in targetVariances)
              {
                var match = unmodifiedVariants.FirstOrDefault(x =>
                     x.Language == variance.Language
                     && x.Version == variance.Version);

                if (match != null)
                {
                  unmodifiedVariants.Remove(match);
                  toPromote.Add(match);
                }
              }
            }
          }
        }

        Emit(new ValidCandidateTargetContext(
            _targetId,
            context.Candidate,
            toPromote.ToArray(),
            toDelete,
            unmodifiedVariants.ToArray()));
      }
    }
  }
}