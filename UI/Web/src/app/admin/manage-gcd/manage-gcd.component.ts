import {ChangeDetectionStrategy, Component, DestroyRef, inject, OnInit, signal} from '@angular/core';
import {FormsModule} from '@angular/forms';
import {takeUntilDestroyed} from '@angular/core/rxjs-interop';
import {TranslocoDirective} from '@jsverse/transloco';
import {finalize, Observable} from 'rxjs';
import {SettingsService} from '../settings.service';

@Component({
  selector: 'app-manage-gcd',
  imports: [FormsModule, TranslocoDirective],
  templateUrl: './manage-gcd.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ManageGcdComponent implements OnInit {
  private readonly settings = inject(SettingsService);
  private readonly destroyRef = inject(DestroyRef);
  readonly configured = signal(false);
  readonly replacement = signal(false);
  readonly busy = signal(false);
  readonly message = signal('');
  username = '';
  password = '';

  ngOnInit() {
    this.run(this.settings.getGcdSettings(), data => {
      this.configured.set(data.configured);
      this.replacement.set(data.requiresReplacement);
    });
  }

  save() {
    this.run(this.settings.saveGcdCredentials(this.username.trim(), this.password), () => {
      this.configured.set(true);
      this.replacement.set(false);
      this.username = this.password = '';
      this.message.set('saved');
    });
  }

  remove() {
    this.run(this.settings.removeGcdCredentials(), () => {
      this.configured.set(false);
      this.replacement.set(false);
      this.username = this.password = '';
      this.message.set('removed');
    });
  }

  test() {
    this.run(this.settings.testGcdConnection(), ok => this.message.set(ok ? 'connected' : 'test-error'));
  }

  private run<T>(request: Observable<T>, done: (value: T) => void) {
    if (this.busy()) return;
    this.busy.set(true);
    this.message.set('');
    request.pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.busy.set(false)))
      .subscribe({next: done, error: () => this.message.set('error')});
  }
}
