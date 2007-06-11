/* 
* Copyright 2004-2005 OpenSymphony 
* 
* Licensed under the Apache License, Version 2.0 (the "License"); you may not 
* use this file except in compliance with the License. You may obtain a copy 
* of the License at 
* 
*   http://www.apache.org/licenses/LICENSE-2.0 
*   
* Unless required by applicable law or agreed to in writing, software 
* distributed under the License is distributed on an "AS IS" BASIS, WITHOUT 
* WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the 
* License for the specific language governing permissions and limitations 
* under the License.
* 
*/

/*
* Previously Copyright (c) 2001-2004 James House
*/
using System;
using System.Threading;

using Common.Logging;

using Quartz.Spi;

namespace Quartz.Core
{
	/// <summary> 
	/// JobRunShell instances are responsible for providing the 'safe' environment
	/// for <see cref="IJob" /> s to run in, and for performing all of the work of
	/// executing the <see cref="IJob" />, catching ANY thrown exceptions, updating
	/// the <see cref="Trigger" /> with the <see cref="IJob" />'s completion code,
	/// etc.
	/// <p>
	/// A <see cref="JobRunShell" /> instance is created by a <see cref="IJobRunShellFactory" />
	/// on behalf of the <see cref="QuartzSchedulerThread" /> which then runs the
	/// shell in a thread from the configured <see cref="ThreadPool" /> when the
	/// scheduler determines that a <see cref="IJob" /> has been triggered.
	/// </p>
	/// </summary>
	/// <seealso cref="IJobRunShellFactory" /> 
	/// <seealso cref="QuartzSchedulerThread" />
	/// <seealso cref="IJob" />
	/// <seealso cref="Trigger" />
	/// <author>James House</author>
	/// <author>Marko Lahma (.NET)</author>
	public class JobRunShell : IThreadRunnable
	{
		private static readonly ILog log = LogManager.GetLogger(typeof (JobRunShell));

		private JobExecutionContext jec = null;
		private QuartzScheduler qs = null;
		private IScheduler scheduler = null;
		private SchedulingContext schdCtxt = null;
		private IJobRunShellFactory jobRunShellFactory = null;
		private bool shutdownRequested = false;


		/// <summary>
		/// Create a JobRunShell instance with the given settings.
		/// </summary>
		/// <param name="jobRunShellFactory">A handle to the <see cref="IJobRunShellFactory" /> that produced
		/// this <see cref="JobRunShell" />.</param>
		/// <param name="scheduler">The <see cref="IScheduler" /> instance that should be made
		/// available within the <see cref="JobExecutionContext" />.</param>
		/// <param name="schdCtxt">the <see cref="SchedulingContext" /> that should be used by the
		/// <see cref="JobRunShell" /> when making updates to the <see cref="IJobStore" />.</param>
		public JobRunShell(IJobRunShellFactory jobRunShellFactory, IScheduler scheduler, SchedulingContext schdCtxt)
		{
			this.jobRunShellFactory = jobRunShellFactory;
			this.scheduler = scheduler;
			this.schdCtxt = schdCtxt;
		}

		/// <summary>
		/// Initializes the job execution context with given scheduler and bundle.
		/// </summary>
		/// <param name="sched">The scheduler.</param>
		/// <param name="firedBundle">The bundle offired triggers.</param>
		public virtual void Initialize(QuartzScheduler sched, TriggerFiredBundle firedBundle)
		{
			qs = sched;

			IJob job;
			JobDetail jobDetail = firedBundle.JobDetail;

			try
			{
				job = sched.JobFactory.NewJob(firedBundle);
			}
			catch (SchedulerException se)
			{
				sched.NotifySchedulerListenersError("An error occured instantiating job to be executed. job= '" + jobDetail.FullName + "'", se);
				throw se;
			}
			catch (Exception e)
			{
				SchedulerException se = new SchedulerException("Problem instantiating type '" + jobDetail.JobType.FullName + "'", e);
				sched.NotifySchedulerListenersError("An error occured instantiating job to be executed. job= '" + jobDetail.FullName + "'", se);
				throw se;
			}

			jec = new JobExecutionContext(scheduler, firedBundle, job);
		}

		/// <summary>
		/// Requests the Shutdown.
		/// </summary>
		public virtual void RequestShutdown()
		{
			shutdownRequested = true;
		}

		/// <summary>
		/// This method has to be implemented in order that starting of the thread causes the object's
		/// run method to be called in that separately executing thread.
		/// </summary>
		public virtual void Run()
		{
			Trigger trigger = jec.Trigger;
			JobDetail jobDetail = jec.JobDetail;

			do
			{
				JobExecutionException jobExEx = null;
				IJob job = jec.JobInstance;

				try
				{
					Begin();
				}
				catch (SchedulerException se)
				{
					qs.NotifySchedulerListenersError("Error executing Job (" + jec.JobDetail.FullName + ": couldn't begin execution.", se);
					break;
				}

				// notify job & trigger listeners...
				try
				{
					if (!NotifyListenersBeginning(jec))
					{
						break;
					}
				}
				catch (VetoedException)
				{
					try
					{
						Complete(true);
					}
					catch (SchedulerException se)
					{
						qs.NotifySchedulerListenersError(
							"Error during veto of Job (" + jec.JobDetail.FullName + ": couldn't finalize execution.", se);
					}
					break;
				}

				DateTime startTime = DateTime.Now;
				DateTime endTime;

				// Execute the job
				try
				{
					log.Debug("Calling Execute on job " + jobDetail.FullName);
					job.Execute(jec);
					endTime = DateTime.Now;
				}
				catch (JobExecutionException jee)
				{
					endTime = DateTime.Now;
					jobExEx = jee;
					log.Info("Job " + jobDetail.FullName + " threw a JobExecutionException: ", jobExEx);
				}
				catch (Exception e)
				{
					endTime = DateTime.Now;
					log.Error("Job " + jobDetail.FullName + " threw an unhandled Exception: ", e);
					SchedulerException se = new SchedulerException("Job threw an unhandled exception.", e);
					se.ErrorCode = SchedulerException.ERR_JOB_EXECUTION_THREW_EXCEPTION;
					qs.NotifySchedulerListenersError("Job (" + jec.JobDetail.FullName + " threw an exception.", se);
					jobExEx = new JobExecutionException(se, false);
					jobExEx.ErrorCode = JobExecutionException.ERR_JOB_EXECUTION_THREW_EXCEPTION;
				}

				jec.JobRunTime = (long) (endTime - startTime).TotalMilliseconds;

				// notify all job listeners
				if (!NotifyJobListenersComplete(jec, jobExEx))
				{
					break;
				}

				int instCode = Trigger.INSTRUCTION_NOOP;

				// update the trigger
				try
				{
					instCode = trigger.ExecutionComplete(jec, jobExEx);
				}
				catch (Exception e)
				{
					// If this happens, there's a bug in the trigger...
					SchedulerException se = new SchedulerException("Trigger threw an unhandled exception.", e);
					se.ErrorCode = SchedulerException.ERR_TRIGGER_THREW_EXCEPTION;
					qs.NotifySchedulerListenersError("Please report this error to the Quartz developers.", se);
				}

				// notify all trigger listeners
				if (!NotifyTriggerListenersComplete(jec, instCode))
				{
					break;
				}

				// update job/trigger or re-Execute job
				if (instCode == Trigger.INSTRUCTION_RE_EXECUTE_JOB)
				{
					jec.IncrementRefireCount();
					try
					{
						Complete(false);
					}
					catch (SchedulerException se)
					{
						qs.NotifySchedulerListenersError(
							"Error executing Job (" + jec.JobDetail.FullName + ": couldn't finalize execution.", se);
					}
					continue;
				}

				try
				{
					Complete(true);
				}
				catch (SchedulerException se)
				{
					qs.NotifySchedulerListenersError(
						"Error executing Job (" + jec.JobDetail.FullName + ": couldn't finalize execution.", se);
					continue;
				}

				try
				{
					qs.NotifyJobStoreJobComplete(schdCtxt, trigger, jobDetail, instCode);
				}
				catch (JobPersistenceException jpe)
				{
					qs.NotifySchedulerListenersError(
						"An error occured while marking executed job complete. job= '" + jobDetail.FullName + "'", jpe);
					if (!CompleteTriggerRetryLoop(trigger, jobDetail, instCode))
					{
					}
					return;
				}

				break;
			} while (true);

			qs.NotifySchedulerThread();

			jobRunShellFactory.ReturnJobRunShell(this);
		}

		/// <summary>
		/// Runs begin procedures on this instance.
		/// </summary>
		protected internal virtual void Begin()
		{
		}

		/// <summary>
		/// Completes the execution.
		/// </summary>
		/// <param name="successfulExecution">if set to <c>true</c> [successful execution].</param>
		protected internal virtual void Complete(bool successfulExecution)
		{
		}

		/// <summary>
		/// Passivates this instance.
		/// </summary>
		public virtual void Passivate()
		{
			jec = null;
			qs = null;
		}

		private bool NotifyListenersBeginning(JobExecutionContext ctx)
		{
			bool vetoed;

			// notify all trigger listeners
			try
			{
				vetoed = qs.NotifyTriggerListenersFired(ctx);
			}
			catch (SchedulerException se)
			{
				qs.NotifySchedulerListenersError(
					"Unable to notify TriggerListener(s) while firing trigger " + "(Trigger and Job will NOT be fired!). trigger= " +
					ctx.Trigger.FullName + " job= " + ctx.JobDetail.FullName, se);

				return false;
			}

			if (vetoed)
			{
				try
				{
					qs.NotifyJobListenersWasVetoed(ctx);
				}
				catch (SchedulerException se)
				{
					qs.NotifySchedulerListenersError(
						"Unable to notify JobListener(s) of vetoed execution " + "while firing trigger (Trigger and Job will NOT be " +
						"fired!). trigger= " + ctx.Trigger.FullName + " job= " + ctx.JobDetail.FullName, se);
				}
				throw new VetoedException(this);
			}

			// notify all job listeners
			try
			{
				qs.NotifyJobListenersToBeExecuted(ctx);
			}
			catch (SchedulerException se)
			{
				qs.NotifySchedulerListenersError(
					"Unable to notify JobListener(s) of Job to be executed: " + "(Job will NOT be executed!). trigger= " +
					ctx.Trigger.FullName + " job= " + ctx.JobDetail.FullName, se);

				return false;
			}

			return true;
		}

		private bool NotifyJobListenersComplete(JobExecutionContext ctx, JobExecutionException jobExEx)
		{
			try
			{
				qs.NotifyJobListenersWasExecuted(ctx, jobExEx);
			}
			catch (SchedulerException se)
			{
				qs.NotifySchedulerListenersError(
					"Unable to notify JobListener(s) of Job that was executed: " + "(error will be ignored). trigger= " +
					ctx.Trigger.FullName + " job= " + ctx.JobDetail.FullName, se);

				return false;
			}

			return true;
		}

		private bool NotifyTriggerListenersComplete(JobExecutionContext ctx, int instCode)
		{
			try
			{
				qs.NotifyTriggerListenersComplete(ctx, instCode);
			}
			catch (SchedulerException se)
			{
				qs.NotifySchedulerListenersError(
					"Unable to notify TriggerListener(s) of Job that was executed: " + "(error will be ignored). trigger= " +
					ctx.Trigger.FullName + " job= " + ctx.JobDetail.FullName, se);

				return false;
			}

			if (!ctx.Trigger.GetNextFireTime().HasValue)
			{
				qs.NotifySchedulerListenersFinalized(ctx.Trigger);
			}

			return true;
		}

		/// <summary>
		/// Completes the trigger retry loop.
		/// </summary>
		/// <param name="trigger">The trigger.</param>
		/// <param name="jobDetail">The job detail.</param>
		/// <param name="instCode">The inst code.</param>
		/// <returns></returns>
		public virtual bool CompleteTriggerRetryLoop(Trigger trigger, JobDetail jobDetail, int instCode)
		{
			while (!shutdownRequested)
			{
				try
				{
					Thread.Sleep(5*1000); // retry every 5 seconds (the db
					// connection must be failed)
					qs.NotifyJobStoreJobComplete(schdCtxt, trigger, jobDetail, instCode);
					return true;
				}
				catch (JobPersistenceException jpe)
				{
					qs.NotifySchedulerListenersError(
						"An error occured while marking executed job complete. job= '" + jobDetail.FullName + "'", jpe);
				}
				catch (ThreadInterruptedException)
				{
				}
			}
			return false;
		}


		[Serializable]
		internal class VetoedException : Exception
		{
			private JobRunShell enclosingInstance;

			public JobRunShell EnclosingInstance
			{
				get { return enclosingInstance; }
			}

			public VetoedException(JobRunShell shell)
			{
				enclosingInstance = shell;
			}
		}
	}
}