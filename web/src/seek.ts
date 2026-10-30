import { controller } from './controller';
type Job = { target?: number; task?: Promise<void>; lastStart: number };
export class SeekQueue {
  private jobs = new Map<string, Job>();
  constructor(private send: (id: string, epoch: string, positionMs: number) => Promise<unknown>) {}
  seek(id: string, epoch: string, positionMs: number): Promise<void> {
    const key = id + ':' + epoch;
    let job = this.jobs.get(key);
    if (!job) {
      job = { lastStart: -Infinity };
      this.jobs.set(key, job);
    }
    job.target = positionMs;
    if (!job.task) {
      const current = job;
      current.task = (async () => {
        while (current.target !== undefined) {
          const delay = 140 - (performance.now() - current.lastStart);
          if (delay > 0) await new Promise((resolve) => setTimeout(resolve, delay));
          const target = current.target;
          current.target = undefined;
          current.lastStart = performance.now();
          await this.send(id, epoch, target);
        }
      })().finally(() => {
        current.task = undefined;
        current.target = undefined;
        if (this.jobs.size > 128)
          for (const [oldKey, old] of this.jobs) {
            if (!old.task && oldKey !== key) this.jobs.delete(oldKey);
          }
      });
    }
    return job.task!;
  }
}
export const seekQueue = new SeekQueue((soundId, epoch, positionMs) =>
  controller.run('seek', { soundId, epoch, positionMs }),
);
