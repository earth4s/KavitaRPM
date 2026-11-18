import {ChangeDetectionStrategy, Component} from '@angular/core';
import {TranslocoDirective} from '@jsverse/transloco';
import {ManageComicVineComponent} from '../manage-comicvine/manage-comicvine.component';

import {ManageGcdComponent} from '../manage-gcd/manage-gcd.component';

/** Provider credentials live in their own panels, under one shared connections page. */
@Component({
  selector: 'app-manage-meta-api-connections',
  imports: [TranslocoDirective, ManageComicVineComponent, ManageGcdComponent],
  templateUrl: './manage-meta-api-connections.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ManageMetaApiConnectionsComponent {}
