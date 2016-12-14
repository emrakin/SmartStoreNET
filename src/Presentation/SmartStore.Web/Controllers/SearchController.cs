﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Mvc;
using SmartStore.Core.Domain.Catalog;
using SmartStore.Core.Domain.Customers;
using SmartStore.Core.Domain.Media;
using SmartStore.Core.Search;
using SmartStore.Services.Common;
using SmartStore.Services.Search;
using SmartStore.Services.Search.Modelling;
using SmartStore.Web.Framework.Controllers;
using SmartStore.Web.Framework.Security;
using SmartStore.Web.Models.Catalog;
using SmartStore.Web.Models.Search;

namespace SmartStore.Web.Controllers
{
	public partial class SearchController : PublicControllerBase
	{
		private readonly CatalogSettings _catalogSettings;
		private readonly MediaSettings _mediaSettings;
		private readonly SearchSettings _searchSettings;
		private readonly ICatalogSearchService _catalogSearchService;
		private readonly IGenericAttributeService _genericAttributeService;
		private readonly CatalogHelper _catalogHelper;
		private readonly ICatalogSearchQueryFactory _queryFactory;

		public SearchController(
			ICatalogSearchQueryFactory queryFactory,
			ICatalogSearchService catalogSearchService,
			CatalogSettings catalogSettings,
			MediaSettings mediaSettings,
			SearchSettings searchSettings,
			IGenericAttributeService genericAttributeService,
			CatalogHelper catalogHelper)
		{
			_queryFactory = queryFactory;
			_catalogSearchService = catalogSearchService;
			_catalogSettings = catalogSettings;
			_mediaSettings = mediaSettings;
			_searchSettings = searchSettings;
			_genericAttributeService = genericAttributeService;
			_catalogHelper = catalogHelper;
		}

		[ChildActionOnly]
		public ActionResult SearchBox()
		{
			var currentTerm = _queryFactory.Current?.Term;

			var model = new SearchBoxModel
			{
				InstantSearchEnabled = _searchSettings.InstantSearchEnabled,
				ShowProductImagesInInstantSearch = _searchSettings.ShowProductImagesInInstantSearch,
				SearchTermMinimumLength = _searchSettings.InstantSearchTermMinLength,
				CurrentQuery = currentTerm
			};

			return PartialView(model);
		}

		[HttpPost]
		public ActionResult InstantSearch(CatalogSearchQuery query)
		{
			if (string.IsNullOrWhiteSpace(query.Term) || query.Term.Length < _searchSettings.InstantSearchTermMinLength)
				return Content(string.Empty);

			// Overwrite search fields
			var searchFields = new List<string> { "name", "shortdescription", "tagname" };

			if (_searchSettings.SearchFields.Contains("sku"))
			{
				searchFields.Add("sku");
			}

			query.Fields = searchFields.ToArray();

			query
				.Slice(0, Math.Min(16, _searchSettings.InstantSearchNumberOfProducts))
				.SortBy(ProductSortingEnum.Relevance);

			var result = _catalogSearchService.Search(query);

			var model = new SearchResultModel(query)
			{
				SearchResult = result,
				Term = query.Term,
				TotalProductsCount = result.Hits.TotalCount
			};

			var mappingSettings = _catalogHelper.GetBestFitProductSummaryMappingSettings(ProductSummaryViewMode.Mini);
			mappingSettings.MapPrices = false;
			// TODO: (mc) actually SHOW pictures in InstantSearch (???)
			mappingSettings.MapPictures = _searchSettings.ShowProductImagesInInstantSearch;
			mappingSettings.ThumbnailSize = _mediaSettings.ProductThumbPictureSizeOnProductDetailsPage;

			var summaryModel = _catalogHelper.MapProductSummaryModel(result.Hits, mappingSettings);

			// Add product hits
			model.TopProducts = summaryModel;

			// Add spell checker suggestions (if any)
			AddSpellCheckerSuggestionsToModel(result.SpellCheckerSuggestions, model);

			// Add top hits (if any)
			AddTopHitsToModel(result.TopCategories, model, "TopCategories", T("Search.TopCategories"), x => new { q = model.Term, c = x.EntityId });
			AddTopHitsToModel(result.TopManufacturers, model, "TopManufacturers", T("Search.TopManufacturers"), x => new { q = model.Term, m = x.EntityId });

			return PartialView(model);
		}

		[RequireHttpsByConfigAttribute(SslRequirement.No)]
		[ValidateInput(false)]
		public ActionResult Search(CatalogSearchQuery query)
		{
			var model = new SearchResultModel(query);

			if (query.Term == null || query.Term.Length < _searchSettings.InstantSearchTermMinLength)
			{
				model.Error = T("Search.SearchTermMinimumLengthIsNCharacters", _searchSettings.InstantSearchTermMinLength);
				return View(model);
			}
			
			// 'Continue shopping' URL
			_genericAttributeService.SaveAttribute(Services.WorkContext.CurrentCustomer,
				SystemCustomerAttributeNames.LastContinueShoppingPage,
				Services.WebHelper.GetThisPageUrl(false),
				Services.StoreContext.CurrentStore.Id);
			
			var result = _catalogSearchService.Search(query);

			if (result.Hits.Count == 0 && result.SpellCheckerSuggestions.Any())
			{
				// No matches, but spell checker made a suggestion.
				// We implicitly search again with the first suggested term.
				var oldSuggestions = result.SpellCheckerSuggestions;
				var oldTerm = query.Term;
				query.Term = oldSuggestions[0];

				result = _catalogSearchService.Search(query);

				if (result.Hits.Any())
				{
					model.AttemptedTerm = oldTerm;
					// Restore the original suggestions.
					result.SpellCheckerSuggestions = oldSuggestions.Where(x => x != query.Term).ToArray();
				}
				else
				{
					query.Term = oldTerm;
				}
			}

			model.SearchResult = result;
			model.Term = query.Term;
			model.TotalProductsCount = result.Hits.TotalCount;

			// TODO: (mc) somehow determine viewmode and call appropriate helper method (Grid or List)
			var mappingSettings = _catalogHelper.GetBestFitProductSummaryMappingSettings(ProductSummaryViewMode.Grid);
			var summaryModel = _catalogHelper.MapProductSummaryModel(result.Hits, mappingSettings);

			// TODO: (mc) Determine and set
			summaryModel.ViewMode = summaryModel.ViewMode;
			summaryModel.AllowViewModeChanging = true;
			summaryModel.AllowSorting = true;
			summaryModel.AllowPagination = true;
			summaryModel.AvailablePageSizes = _catalogSettings.DefaultPageSizeOptions.Convert<List<int>>();

			// Add product hits
			model.TopProducts = summaryModel;

			// Add spell checker suggestions (if any)
			AddSpellCheckerSuggestionsToModel(result.SpellCheckerSuggestions, model);

			// Add top hits (if any)
			AddTopHitsToModel(result.TopCategories, model, "TopCategories", T("Search.TopCategories"), x => new { q = model.Term, c = x.EntityId });
			AddTopHitsToModel(result.TopManufacturers, model, "TopManufacturers", T("Search.TopManufacturers"), x => new { q = model.Term, m = x.EntityId });

			return View(model);
		}

		private void AddSpellCheckerSuggestionsToModel(string[] suggestions, SearchResultModel model)
		{
			if (suggestions.Length == 0)
				return;

			var hitGroup = new SearchResultModel.HitGroup(model)
			{
				Name = "SpellChecker",
				DisplayName = T("Search.DidYouMean"),
				Ordinal = -100
			};

			hitGroup.Hits.AddRange(suggestions.Select(x => new SearchResultModel.HitItem
			{
				Label = x,
				Url = Url.RouteUrl("Search", new { q = x })
			}));

			model.HitGroups.Add(hitGroup);
		}

		private void AddTopHitsToModel(
			IEnumerable<ISearchHit> hits,
			SearchResultModel model,
			string name,
			string displayName,
			Func<ISearchHit, object> routeValues)
		{
			if (!hits.Any())
				return;

			var hitGroup = new SearchResultModel.HitGroup(model)
			{
				Name = name,
				DisplayName = displayName,
				Ordinal = -100
			};

			foreach (var hit in hits)
			{
				string label = null;

				if (model.Query.LanguageSeoCode.HasValue())
				{
					label = hit.GetString("name", model.Query.LanguageSeoCode);
				}
				if (label.IsEmpty())
				{
					label = hit.GetString("name");
				}

				hitGroup.Hits.Add(new SearchResultModel.HitItem
				{
					Label = label,
					Url = Url.RouteUrl("Search", routeValues(hit))
				});
			}

			model.HitGroups.Add(hitGroup);
		}
	}
}