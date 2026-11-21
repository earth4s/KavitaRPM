import {ChangeDetectionStrategy, Component, DestroyRef, inject, OnInit, signal} from '@angular/core';
import {FormsModule} from '@angular/forms';
import {takeUntilDestroyed} from '@angular/core/rxjs-interop';
import {TranslocoDirective} from '@jsverse/transloco';
import {finalize} from 'rxjs';
import {SettingsService} from '../settings.service';
import {ComicVineSettings} from '../_models/comicvine-settings';
import {LoadingComponent} from '../../shared/loading/loading.component';

@Component({
  selector: 'app-manage-comicvine',
  imports: [FormsModule, TranslocoDirective, LoadingComponent],
  templateUrl: './manage-comicvine.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ManageComicVineComponent implements OnInit {
  private readonly settingsService = inject(SettingsService);
  private readonly destroyRef = inject(DestroyRef);

  readonly settings = signal<ComicVineSettings | null>(null);
  readonly apiKey = signal('');
  readonly showKey = signal(false);
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  ngOnInit() {
    this.load();
  }

  load() {
    this.loading.set(true);
    this.error.set(null);
    this.settingsService.getComicVineSettings().pipe(
      takeUntilDestroyed(this.destroyRef),
      finalize(() => this.loading.set(false))
    ).subscribe({
      next: settings => this.settings.set(settings),
      error: () => this.error.set('load-error')
    });
  }

  save() {
    const key = this.apiKey().trim();
    if (this.busy() || !key || key.length > 256 || /\s/.test(key)) return;
    this.beginAction();
    this.settingsService.saveComicVineSettings(key).pipe(
      takeUntilDestroyed(this.destroyRef),
      finalize(() => this.busy.set(false))
    ).subscribe({
      next: settings => {
        this.settings.set(settings);
        this.apiKey.set('');
        this.showKey.set(false);
        this.message.set('saved');
      },
      error: () => this.error.set('save-error')
    });
  }

  remove() {
    if (this.busy() || !this.settings()?.hasSavedApiKey) return;
    this.beginAction();
    this.settingsService.removeComicVineSettings().pipe(
      takeUntilDestroyed(this.destroyRef),
      finalize(() => this.busy.set(false))
    ).subscribe({
      next: settings => {
        this.settings.set(settings);
        this.apiKey.set('');
        this.showKey.set(false);
        this.message.set('removed');
      },
      error: () => this.error.set('remove-error')
    });
  }

  testConnection() {
    if (this.busy() || !this.settings()?.hasApiKey) return;
    this.beginAction();
    this.settingsService.testComicVineConnection().pipe(
      takeUntilDestroyed(this.destroyRef),
      finalize(() => this.busy.set(false))
    ).subscribe({
      next: result => result.success ? this.message.set('test-success') : this.error.set('test-error'),
      error: () => this.error.set('test-error')
    });
  }

  private beginAction() {
    this.busy.set(true);
    this.message.set(null);
    this.error.set(null);
  }
}
