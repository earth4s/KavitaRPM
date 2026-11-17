import {ChangeDetectionStrategy, Component, computed, DestroyRef, inject, OnInit, signal} from '@angular/core';
import {translate, TranslocoDirective} from "@jsverse/transloco";
import {takeUntilDestroyed, toObservable} from "@angular/core/rxjs-interop";
import {LibraryTypePipe} from "../../../_pipes/library-type.pipe";
import {ImageComponent} from "../../../shared/image/image.component";
import {MatchStateOptionPipe} from "../../../_pipes/match-state.pipe";
import {UtcToLocalTimePipe} from "../../../_pipes/utc-to-local-time.pipe";
import {DefaultValuePipe} from "../../../_pipes/default-value.pipe";
import {NgxDatatableModule} from "@siemens/ngx-datatable";
import {LibraryNamePipe} from "../../../_pipes/library-name.pipe";
import {APP_BASE_HREF, AsyncPipe, DecimalPipe, PercentPipe} from "@angular/common";
import {ResponsiveTableComponent} from "../../../shared/_components/responsive-table/responsive-table.component";
import {LibraryType} from "../../../_models/library/library";
import {ActionService} from "../../../_services/action.service";
import {ManageService} from "../../../_services/manage.service";
import {EVENTS, MessageHubService} from "../../../_services/message-hub.service";
import {ManageMatchSeries, MatchedExternalSeriesCount} from "../../../_models/kavitaplus/manage-match-series";
import {Series} from "../../../_models/series";
import {ManageMatchFilter} from "../../../_models/kavitaplus/manage-match-filter";
import {catchError, debounceTime, distinctUntilChanged, of, skip, tap} from "rxjs";
import {LicenseService} from "../../../_services/license.service";
import {MetadataProvider} from "../../../_models/kavitaplus/metadata-provider.enum";
import {switchMap} from "rxjs/operators";
import {ExternalMatchRateLimitErrorEvent} from "../../../_models/events/external-match-rate-limit-error-event";
import {ToastrService} from '@openng/ngx-toastr';
import {ImageService} from "../../../_services/image.service";
import {Pagination} from "../../../_models/pagination";
import {ScanSeriesEvent} from "../../../_models/events/scan-series-event";
import {allMatchStates, MatchStateOption} from "../../../_models/kavitaplus/match-state-option";
import {LibraryService} from "../../../_services/library.service";
import {LoadingComponent} from "../../../shared/loading/loading.component";
import {form, FormField} from "@angular/forms/signals";
import {
  EnumOption,
  SettingSelectComponent
} from "../../../settings/_components/setting-enum-select/setting-select.component";


interface FormModel {
  source: 'comicvine' | 'kavitaplus';
  matchState: MatchStateOption;
  libraryType: LibraryType;
}

@Component({
  selector: 'app-manage-matched-metadata',
  imports: [
    TranslocoDirective,
    ImageComponent,
    MatchStateOptionPipe,
    UtcToLocalTimePipe,
    DefaultValuePipe,
    NgxDatatableModule,
    LibraryNamePipe,
    AsyncPipe,
    LibraryTypePipe,
    ResponsiveTableComponent,
    LoadingComponent,
    PercentPipe,
    DecimalPipe,
    FormField,
    SettingSelectComponent,
  ],
  templateUrl: './manage-matched-metadata.component.html',
  styleUrl: './manage-matched-metadata.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ManageMatchedMetadataComponent implements OnInit {

  protected readonly allMatchStates = computed(() => this.isComicVine()
    ? allMatchStates.filter(m => m !== MatchStateOption.Error)
    : allMatchStates.filter(m => m !== MatchStateOption.Matched));
  protected readonly licenseService = inject(LicenseService);
  protected readonly sources = computed(() => this.licenseService.hasActiveLicense()
    ? ["comicvine", "kavitaplus"] : []);
  protected readonly isComicVine = computed(() => this.formGroup.source().value() === "comicvine");
  private requestNumber = 0;
  private countRequest = 0;

  private readonly actionService = inject(ActionService);
  private readonly manageService = inject(ManageService);
  private readonly messageHub = inject(MessageHubService);
  private readonly toastr = inject(ToastrService);
  protected readonly imageService = inject(ImageService);
  protected readonly baseUrl = inject(APP_BASE_HREF);
  protected readonly destroyRef = inject(DestroyRef);
  private readonly libraryService = inject(LibraryService);

  private paidLibraryTypes = signal<LibraryType[]>([]);
  metadataEnabledLibraryTypes = computed<EnumOption<LibraryType>[]>(() => [
    ...(this.isComicVine() ? [LibraryType.Comic, LibraryType.ComicVine] : this.paidLibraryTypes()).map(value => ({value})),
    {value: -1 as LibraryType}
  ]);
  isLoading = signal(true);
  data = signal<ManageMatchSeries[]>([]);
  pagination = signal<Pagination>({
    currentPage: 1,
    totalItems: 0,
    totalPages: 0,
    itemsPerPage: 15,
  });

  private readonly formModel = signal<FormModel>({
    source: "comicvine",
    matchState: MatchStateOption.NotMatched,
    libraryType: -1 as LibraryType // Denotes all
  });
  formGroup = form(this.formModel);
  trackBy = (idx: number, item: ManageMatchSeries) => `${item.isMatched}_${item.series.name}_${idx}`;

  matchedCounts = signal<MatchedExternalSeriesCount | null>(null);
  totalCount = signal<number>(0);
  percentageMatched = computed(() => {
    const matchedCount = this.matchedCounts();
    if (!matchedCount) { return null }

    const totalItems = matchedCount.totalCount;

    if (totalItems == 0) return 0;

    return  (totalItems - (matchedCount.dontMatchCount + matchedCount.erroredCount + matchedCount.notMatchedCount)) / totalItems;
  });
  isMatchedState = computed(() => {
    return this.formGroup.matchState().value() === MatchStateOption.Matched;
  });

  constructor() {
    toObservable(this.formGroup.source().value).pipe(skip(1), takeUntilDestroyed()).subscribe(() => {
      this.formModel.update(value => ({...value, libraryType: -1 as LibraryType, matchState: MatchStateOption.NotMatched}));
      this.data.set([]);
      this.matchedCounts.set(null);
    });
    toObservable(this.formModel).pipe(
      debounceTime(300),
      distinctUntilChanged(),
      skip(1),
      switchMap(() => {
        this.refreshCounts();
        return this.loadData();
      }),
      takeUntilDestroyed(this.destroyRef)
    ).subscribe();
  }

  ngOnInit() {
    if (this.licenseService.hasActiveLicense()) this.libraryService.getLibraryTypesWithMetadataSupport().pipe(
      takeUntilDestroyed(this.destroyRef),
      tap(types => this.paidLibraryTypes.set(types))
    ).subscribe();

    this.messageHub.messages$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(message => {
      if (message.event == EVENTS.ScanSeries) {
        const evt = message.payload as ScanSeriesEvent;
        if (this.data().filter(d => d.series.id === evt.seriesId).length > 0) {
          this.loadData().subscribe();
          this.refreshCounts();
        }
      }

      if (message.event == EVENTS.ExternalMatchRateLimitError) {
        const evt = message.payload as ExternalMatchRateLimitErrorEvent;
        this.toastr.error(translate('toasts.external-match-rate-error', {seriesName: evt.seriesName}))
      }
    });

    this.refreshCounts();

    this.loadData().subscribe();
  }

  onPageChange(page: number) {
    this.loadData(page + 1).subscribe();
  }

  loadData(pageNumber: number = 1) {
    const request = ++this.requestNumber;
    const source = this.formGroup.source().value();
    const filter: ManageMatchFilter = {
      matchStateOption: this.formGroup.matchState().value(),
      libraryType: this.formGroup.libraryType().value(),
      searchTerm: ''
    };

    this.isLoading.set(true);

    return this.manageService.getAllKavitaPlusSeries(filter, pageNumber, this.pagination().itemsPerPage, this.isComicVine()).pipe(tap(data => {
      if (request !== this.requestNumber || source !== this.formGroup.source().value()) return;
      this.data.set(data.result);
      this.pagination.set({
        itemsPerPage: data.pagination.itemsPerPage,
        totalItems: data.pagination.totalItems,
        totalPages: data.pagination.totalPages,
        currentPage: data.pagination.currentPage - 1, // ngx-datatable is 0 based, Kavita is 1 based
      });
      this.isLoading.set(false);

    }), catchError(() => {
      if (request === this.requestNumber) this.isLoading.set(false);
      return of(null);
    }));
  }

  private refreshCounts() {
    const request = ++this.countRequest;
    const source = this.formGroup.source().value();
    this.manageService.getMatchedExternalSeriesCount(this.isComicVine()).subscribe(res => {
      if (request === this.countRequest && source === this.formGroup.source().value()) this.matchedCounts.set(res);
    });
  }

  fixMatch(series: Series) {
    this.actionService.matchSeries(series, result => {
      if (!result) return;

      this.loadData().subscribe();
      this.refreshCounts();
    }, this.isComicVine() ? MetadataProvider.ComicVine : undefined);
  }

  protected readonly MatchStateOption = MatchStateOption;
}
