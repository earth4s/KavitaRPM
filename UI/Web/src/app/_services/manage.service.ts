import {inject, Injectable} from '@angular/core';
import {environment} from "../../environments/environment";
import {HttpClient, HttpParams} from "@angular/common/http";
import {ManageMatchSeries, MatchedExternalSeriesCount} from "../_models/kavitaplus/manage-match-series";
import {ManageMatchFilter} from "../_models/kavitaplus/manage-match-filter";
import {UtilityService} from "../shared/_services/utility.service";
import {map} from "rxjs/operators";

@Injectable({
  providedIn: 'root'
})
export class ManageService {

  baseUrl = environment.apiUrl;
  private readonly httpClient = inject(HttpClient);
  private readonly utilityService = inject(UtilityService);

  getAllKavitaPlusSeries(filter: ManageMatchFilter, pageNum?: number, itemsPerPage?: number, source: 'comicvine' | 'gcd' | 'kavitaplus' = 'kavitaplus') {
    const params = this.utilityService.addPaginationIfExists(new HttpParams(), pageNum, itemsPerPage);

    return this.httpClient.post<Array<ManageMatchSeries>>(this.baseUrl + (source !== 'kavitaplus' ? source + '/series' : "manage/series-metadata"), filter,
      {observe: 'response', params}).pipe(
        map(res => {
          return this.utilityService.createPaginatedResult<ManageMatchSeries>(res)
        }),
    );
  }

  getMatchedExternalSeriesCount(source: 'comicvine' | 'gcd' | 'kavitaplus' = 'kavitaplus') {
    return this.httpClient.get<MatchedExternalSeriesCount>(this.baseUrl + (source !== 'kavitaplus' ? source + '/counts' : "manage/matched-series-counts"));
  }
}
