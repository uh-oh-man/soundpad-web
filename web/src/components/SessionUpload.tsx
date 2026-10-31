import { useState } from 'react';
import { Upload, LoaderCircle } from 'lucide-react';
import { controller } from '../controller';
export function SessionUpload({ disabled }: { disabled: boolean }) {
  const [pending, setPending] = useState(false),
    [error, setError] = useState('');
  return (
    <div className="session-upload">
      <label className={`upload-audio ${pending ? 'upload-pending' : ''}`}>
        {pending ? <LoaderCircle className="spinning" size={14} /> : <Upload size={14} />}{' '}
        {pending ? 'Processing audio…' : 'Add session audio'}
        <input
          type="file"
          aria-label="Add session audio"
          accept="audio/*,.ogg,.flac"
          disabled={disabled || pending}
          onChange={(e) => {
            const file = e.currentTarget.files?.[0];
            e.currentTarget.value = '';
            if (!file) return;
            setPending(true);
            setError('');
            void controller
              .upload(file)
              .catch((reason) => setError(String(reason.message)))
              .finally(() => setPending(false));
          }}
        />
      </label>
      <small>Temporary until the host approves</small>
      {error && (
        <p className="inline-error" role="alert">
          {error} Choose the file again to retry.
        </p>
      )}
    </div>
  );
}
